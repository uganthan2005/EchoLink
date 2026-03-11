package main

/*
#include <stdlib.h>
#include <string.h>
*/
import "C"
import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"crypto/x509"
	"encoding/json"
	"encoding/pem"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"strings"
	"sync"

	"github.com/armon/go-socks5"
	"github.com/pkg/sftp"
	"golang.org/x/crypto/ssh"
	"tailscale.com/ipn/ipnstate"
	"tailscale.com/net/netmon"
	"tailscale.com/tsnet"
)
// REAL Tailscale flags to bypass Android 11+ SELinux Netlink restrictions.
func init() {
	os.Setenv("TS_DISABLE_LINUX_ROUTING", "true")
	os.Setenv("TS_ANDROID_ALLOW_UNCONFIGURED_ROUTING", "true")
	os.Setenv("TS_DEBUG_NETSTACK", "true")
}

var (
	tsServer      *tsnet.Server
	sshLn         net.Listener
	mu            sync.Mutex
	internalState string = "NotStarted"
	lastAuthUrl   string = ""
	lastErrorMsg  string = ""
)

//export StartEchoLinkNode
func StartEchoLinkNode(configDir *C.char, authKey *C.char, hostname *C.char, localIp *C.char) int {
	mu.Lock()
	defer mu.Unlock()

	if tsServer != nil {
		return 0
	}

	internalState = "Starting"
	lastErrorMsg = ""

	conf := C.GoString(configDir)
	host := C.GoString(hostname)
	key := C.GoString(authKey)
	ipStr := C.GoString(localIp)

	if host == "" {
		host = "echolink-android"
	}

	log.Printf("[Go] Starting node: Host=%s, Dir=%s, LocalIP=%s", host, conf, ipStr)

	// Dynamically register the interface getter with the IP C# gave us
	netmon.RegisterInterfaceGetter(func() ([]netmon.Interface, error) {
		var addrs []net.Addr

		if ipStr != "" && ipStr != "127.0.0.1" {
			parsedIp := net.ParseIP(ipStr)
			if parsedIp != nil {
				addrs = append(addrs, &net.IPNet{IP: parsedIp, Mask: net.CIDRMask(24, 32)})
			}
		}

		return []netmon.Interface{
			{
				Interface: &net.Interface{Index: 1, Name: "csharp-bridge", Flags: net.FlagUp},
				AltAddrs:  addrs,
			},
		}, nil
	})

	os.Setenv("TS_LOG_TARGET", "discard")
	os.Setenv("TS_LOGTAIL_STATE_DIR", conf)
	os.Setenv("HOME", conf)
	os.Setenv("XDG_CACHE_HOME", conf)

	tsServer = &tsnet.Server{
		Dir:        conf,
		Hostname:   host,
		AuthKey:    key,
		ControlURL: "https://echo-link.app",
		Ephemeral:  false,
		Logf: func(format string, args ...any) {
			msg := fmt.Sprintf(format, args...)
			if strings.Contains(msg, "https://") {
				idx := strings.Index(msg, "https://")
				lastAuthUrl = msg[idx:]
				internalState = "NeedsLogin"
			}
			log.Printf("[tsnet] %s", msg)
		},
	}

	go func() {
		_, err := tsServer.Up(context.Background())
		if err == nil {
			// THE SMOKING GUN FIX: All services MUST run concurrently!
			go startSftpServer()
			go startPairingForwarder()
			go startSocks5Proxy()

			internalState = "Running"
		} else {
			log.Printf("[Go] tsServer.Up error: %v", err)
			lastErrorMsg = fmt.Sprintf("tsnet.Up error: %v", err)
			internalState = "Error"
		}
	}()

	return 1
}

//export GetLastErrorMsg
func GetLastErrorMsg() *C.char {
	return C.CString(lastErrorMsg)
}

func startSocks5Proxy() {
	conf := &socks5.Config{
		Dial: func(ctx context.Context, network, addr string) (net.Conn, error) {
			return tsServer.Dial(ctx, network, addr)
		},
	}

	server, err := socks5.New(conf)
	if err != nil {
		log.Printf("[Go] Failed to initialize SOCKS5 proxy: %v", err)
		return
	}

	log.Println("[Go] SOCKS5 proxy running on 127.0.0.1:1055 (C# -> Mesh bridge)")
	if err := server.ListenAndServe("tcp", "127.0.0.1:1055"); err != nil {
		log.Printf("[Go] SOCKS5 proxy crashed: %v", err)
	}
}

func startPairingForwarder() {
	ln, err := tsServer.Listen("tcp", ":44444")
	if err != nil {
		log.Printf("[Go] Failed to listen on mesh port 44444: %v", err)
		return
	}

	log.Printf("[Go] Pairing Forwarder listening on mesh port 44444, routing to 127.0.0.1:44444")

	for {
		meshConn, err := ln.Accept()
		if err != nil {
			log.Printf("[Go] Pairing forwarder accept error: %v", err)
			return
		}

		go func(c net.Conn) {
			defer c.Close()
			log.Printf("[Go] Received pairing connection from mesh: %s", c.RemoteAddr().String())

			localConn, err := net.Dial("tcp", "127.0.0.1:44444")
			if err != nil {
				log.Printf("[Go] Failed to dial local C# pairing service: %v", err)
				return
			}
			defer localConn.Close()

			go io.Copy(c, localConn)
			io.Copy(localConn, c)
		}(meshConn)
	}
}

func startSftpServer() {
	mu.Lock()
	if sshLn != nil {
		mu.Unlock()
		return
	}
	mu.Unlock()

	ln, err := tsServer.Listen("tcp", ":2222")
	if err != nil {
		log.Printf("[Go] Failed to listen on :2222: %v", err)
		return
	}
	sshLn = ln

	config := &ssh.ServerConfig{
		NoClientAuth: true,
	}

	// Load or generate a host key (REQUIRED for the SSH server to actually accept connections)
	keyPath := tsServer.Dir + "/ssh_host_ed25519_key"
	privateBytes, err := os.ReadFile(keyPath)
	if err != nil {
		log.Printf("[Go] Generating new host key at %s", keyPath)
		// We use a dummy ed25519 key for the ephemeral server, or generate a real one.
		// For simplicity in a c-shared lib without external keygen tools, we can generate a small RSA key
		// or just use a statically compiled one for the internal tunnel since Tailscale provides the real security.
		
		// To keep it simple and robust, let's use a quick RSA generation
		privateKey, err := rsa.GenerateKey(rand.Reader, 2048)
		if err == nil {
			privateBytes = pem.EncodeToMemory(&pem.Block{
				Type:  "RSA PRIVATE KEY",
				Bytes: x509.MarshalPKCS1PrivateKey(privateKey),
			})
			os.WriteFile(keyPath, privateBytes, 0600)
		}
	}

	private, err := ssh.ParsePrivateKey(privateBytes)
	if err == nil {
		config.AddHostKey(private)
		log.Printf("[Go] Host key loaded successfully.")
	} else {
		log.Printf("[Go] CRITICAL: Failed to parse host key: %v. SSH will reject all connections!", err)
	}

	log.Printf("[Go] SFTP Server listening on :2222")

	for {
		conn, err := sshLn.Accept()
		if err != nil {
			return
		}
		go handleSshConn(conn, config)
	}
}

func handleSshConn(nConn net.Conn, config *ssh.ServerConfig) {
	_, chans, reqs, err := ssh.NewServerConn(nConn, config)
	if err != nil {
		return
	}
	go ssh.DiscardRequests(reqs)

	for newChannel := range chans {
		if newChannel.ChannelType() != "session" {
			newChannel.Reject(ssh.UnknownChannelType, "unknown channel type")
			continue
		}
		channel, requests, _ := newChannel.Accept()

		go func(in <-chan *ssh.Request) {
			for req := range in {
				if req.Type == "subsystem" && string(req.Payload[4:]) == "sftp" {
					req.Reply(true, nil)
					// Use a real file-system backed server instead of InMemHandler
					server, err := sftp.NewServer(channel)
					if err == nil {
						if err := server.Serve(); err != nil && err != io.EOF {
							log.Print("[Go] SFTP error:", err)
						}
					} else {
						log.Print("[Go] Failed to init SFTP server:", err)
					}
					return
				}
				req.Reply(false, nil)
			}
		}(requests)
	}
}

type Device struct {
	Name       string `json:"Name"`
	IpAddress  string `json:"IpAddress"`
	IsOnline   bool   `json:"IsOnline"`
	DeviceType string `json:"DeviceType"`
	Os         string `json:"Os"`
}

func getStatus() (*ipnstate.Status, error) {
	if tsServer == nil {
		return nil, fmt.Errorf("not started")
	}
	lc, err := tsServer.LocalClient()
	if err != nil {
		return nil, err
	}
	return lc.Status(context.Background())
}

//export GetPeerListJson
func GetPeerListJson() *C.char {
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("[]")
	}

	var devices []Device
	for _, peer := range status.Peer {
		ip := ""
		if len(peer.TailscaleIPs) > 0 {
			ip = peer.TailscaleIPs[0].String()
		}

		devices = append(devices, Device{
			Name:       peer.HostName,
			IpAddress:  ip,
			IsOnline:   peer.Online,
			DeviceType: "Desktop",
			Os:         peer.OS,
		})
	}

	data, _ := json.Marshal(devices)
	return C.CString(string(data))
}

//export GetBackendState
func GetBackendState() *C.char {
	if internalState == "Starting" || internalState == "Error" {
		return C.CString(internalState)
	}

	status, err := getStatus()
	if err != nil {
		return C.CString(internalState)
	}

	if len(status.TailscaleIPs) > 0 && status.BackendState == "Running" {
		internalState = "Running"
		return C.CString("Running")
	}

	if status.AuthURL != "" {
		lastAuthUrl = status.AuthURL
		internalState = "NeedsLogin"
		return C.CString("NeedsLogin")
	}

	if status.BackendState != "" {
		internalState = status.BackendState
	}

	return C.CString(internalState)
}

//export GetTailscaleIp
func GetTailscaleIp() *C.char {
	status, err := getStatus()
	if err != nil || status == nil || len(status.TailscaleIPs) == 0 {
		return C.CString("")
	}
	return C.CString(status.TailscaleIPs[0].String())
}

//export GetLoginUrl
func GetLoginUrl() *C.char {
	if lastAuthUrl != "" {
		return C.CString(lastAuthUrl)
	}
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("")
	}
	return C.CString(status.AuthURL)
}

//export LogoutNode
func LogoutNode() {
	mu.Lock()
	defer mu.Unlock()
	if tsServer == nil {
		return
	}
	lc, err := tsServer.LocalClient()
	if err == nil {
		log.Printf("[Go] Triggering Logout...")
		lc.Logout(context.Background())
		internalState = "NeedsLogin"
	}
}

//export StopEchoLinkNode
func StopEchoLinkNode() {
	mu.Lock()
	defer mu.Unlock()
	internalState = "NotStarted"
	if sshLn != nil {
		sshLn.Close()
		sshLn = nil
	}
	if tsServer != nil {
		tsServer.Close()
		tsServer = nil
	}
}

func main() {}
