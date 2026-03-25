package main

/*
#include <stdlib.h>
#include <string.h>

#ifdef __ANDROID__
#include <android/log.h>
static inline void android_log(const char* msg) {
    __android_log_print(ANDROID_LOG_INFO, "EchoLink-Go", "%s", msg);
}
#else
static inline void android_log(const char* msg) {
    // No-op or stdout for non-android
}
#endif
*/
import "C"
import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"strings"
	"sync"
	"time"
	"unsafe"

	"github.com/bendahl/uinput"
	"github.com/armon/go-socks5"
	"github.com/gliderlabs/ssh"
	"github.com/pkg/sftp"
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
	tsServer         *tsnet.Server
	mu               sync.Mutex
	internalState    string = "Stopped"
	lastAuthUrl      string = ""
	lastErrorMsg     string = ""
	audioTarget      string = ""
	tempSSHPasswords = make(map[string]string)
	sshMu            sync.Mutex
	virtualMouse    uinput.Mouse
	virtualKeyboard uinput.Keyboard
)

//export InitializeVirtualMouse
func InitializeVirtualMouse() int {
	var err error
	_, err = os.OpenFile("/dev/uinput", os.O_WRONLY|os.O_SYNC, 0660)
	if err != nil {
		log.Printf("[Go] Cannot open /dev/uinput: %v", err)
		return 0
	}

	virtualMouse, err = uinput.CreateMouse("/dev/uinput", []byte("EchoLink Virtual Mouse"))
	if err != nil {
		log.Printf("[Go] Failed to create virtual mouse: %v", err)
		return 0
	}

	log.Printf("[Go] Virtual mouse initialized successfully")
	return 1
}

//export InitializeVirtualKeyboard
func InitializeVirtualKeyboard() int {
	var err error
	virtualKeyboard, err = uinput.CreateKeyboard("/dev/uinput", []byte("EchoLink Virtual Keyboard"))
	if err != nil {
		log.Printf("[Go] Failed to create virtual keyboard: %v", err)
		return 0
	}

	log.Printf("[Go] Virtual keyboard initialized successfully")
	return 1
}

//export InitializeVirtualInput
func InitializeVirtualInput() int {
	res1 := InitializeVirtualMouse()
	res2 := InitializeVirtualKeyboard()
	if res1 == 1 && res2 == 1 {
		return 1
	}
	return 0
}

//export SendMouseRelative
func SendMouseRelative(dx C.int, dy C.int) {
	if virtualMouse != nil {
		virtualMouse.Move(int32(dx), int32(dy))
	}
}

//export SendMouseClick
func SendMouseClick(button C.int, state C.int) {
	if virtualMouse == nil {
		return
	}
	// button: 0=left, 1=right
	// state: 1=down, 0=up
	switch button {
	case 0:
		if state == 1 {
			virtualMouse.LeftPress()
		} else {
			virtualMouse.LeftRelease()
		}
	case 1:
		if state == 1 {
			virtualMouse.RightPress()
		} else {
			virtualMouse.RightRelease()
		}
	}
}

//export SendMouseAction
func SendMouseAction(button C.int, state C.int) {
	if virtualMouse == nil {
		return
	}
	// button: 0=left, 1=right
	// state: 1=down, 0=up
	switch button {
	case 0:
		if state == 1 {
			virtualMouse.LeftPress()
		} else {
			virtualMouse.LeftRelease()
		}
	case 1:
		if state == 1 {
			virtualMouse.RightPress()
		} else {
			virtualMouse.RightRelease()
		}
	}
}

//export StartEchoLinkNode
func StartEchoLinkNode(configDir *C.char, authKey *C.char, hostname *C.char, localIp *C.char, isEphemeral C.int) int {
	mu.Lock()
	defer mu.Unlock()

	log.Printf("[Go] StartEchoLinkNode called.")

	if tsServer != nil {
		log.Printf("[Go] Server instance already exists. Returning 0.")
		return 0
	}

	internalState = "Starting"
	lastErrorMsg = ""
	lastAuthUrl = ""

	conf := C.GoString(configDir)
	host := C.GoString(hostname)
	key := C.GoString(authKey)
	ipStr := C.GoString(localIp)
	ephemeral := isEphemeral != 0

	if host == "" {
		host = "echolink-android"
	}

	log.Printf("[Go] CONFIG: Host=%s, Dir=%s, LocalIP=%s, Ephemeral=%v, KeyLen=%d", host, conf, ipStr, ephemeral, len(key))

	if err := os.MkdirAll(conf, 0755); err != nil {
		log.Printf("[Go] CRITICAL: Failed to create config dir: %v", err)
		lastErrorMsg = fmt.Sprintf("MkdirAll failed: %v", err)
		internalState = "Error"
		return -1
	}

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
		ControlURL: "https://control.echo-link.app",
		Ephemeral:  ephemeral,
		Logf: func(format string, args ...any) {
			msg := fmt.Sprintf(format, args...)
			if strings.Contains(msg, "https://") && strings.Contains(msg, "/a/") && internalState != "Running" {
				idx := strings.Index(msg, "https://")
				urlPart := msg[idx:]
				if spaceIdx := strings.Index(urlPart, " "); spaceIdx != -1 {
					urlPart = urlPart[:spaceIdx]
				}
				lastAuthUrl = urlPart
				internalState = "NeedsLogin"
				log.Printf("[Go] AUTH REQUIRED: %s", lastAuthUrl)
			}
			
			// Send to Logcat
			cmsg := C.CString(fmt.Sprintf("[tsnet] %s", msg))
			C.android_log(cmsg)
			C.free(unsafe.Pointer(cmsg))
		},
	}

	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
		defer cancel()

		status, err := tsServer.Up(ctx)
		if err == nil {
			log.Printf("[Go] tsServer.Up() SUCCESS! IP: %v", status.TailscaleIPs)
			internalState = "Running"
			go startSocks5Proxy()
			go startPairingForwarder()
			go startInternalSshServer()
			go startUnifiedAppForwarder()
		} else {
			log.Printf("[Go] tsServer.Up() FAILED: %v", err)
			lastErrorMsg = fmt.Sprintf("tsnet.Up error: %v", err)
			if internalState != "NeedsLogin" {
				internalState = "Error"
			}
		}
	}()

	return 1
}

//export GetBackendState
func GetBackendState() *C.char {
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
	return C.CString(lastAuthUrl)
}

//export GetLastErrorMsg
func GetLastErrorMsg() *C.char {
	return C.CString(lastErrorMsg)
}

//export LogoutNode
func LogoutNode() {
	mu.Lock()
	defer mu.Unlock()
	if tsServer != nil {
		if lc, err := tsServer.LocalClient(); err == nil {
			lc.Logout(context.Background())
		}
		tsServer.Close()
		tsServer = nil
		internalState = "Stopped"
		lastAuthUrl = ""
	}
}

//export GetPeerListJson
func GetPeerListJson() *C.char {
	status, err := getStatus()
	if err != nil || status == nil {
		return C.CString("[]")
	}

	var devices []Device
	if status.Self != nil {
		selfIp := ""
		if len(status.Self.TailscaleIPs) > 0 {
			selfIp = status.Self.TailscaleIPs[0].String()
		}
		var selfTags []string
		if status.Self.Tags != nil {
			selfTags = status.Self.Tags.AsSlice()
		}
		devices = append(devices, Device{
			Name:       status.Self.HostName + " (This Device)",
			IpAddress:  selfIp,
			IsOnline:   true,
			DeviceType: "Mobile",
			Os:         status.Self.OS,
			UserID:     fmt.Sprintf("%d", status.Self.UserID),
			Tags:       selfTags,
			IsSelf:     true,
		})
	}

	for _, peer := range status.Peer {
		ip := ""
		if len(peer.TailscaleIPs) > 0 {
			ip = peer.TailscaleIPs[0].String()
		}
		var peerTags []string
		if peer.Tags != nil {
			peerTags = peer.Tags.AsSlice()
		}
		devices = append(devices, Device{
			Name:       peer.HostName,
			IpAddress:  ip,
			IsOnline:   peer.Online,
			DeviceType: "Desktop",
			Os:         peer.OS,
			UserID:     fmt.Sprintf("%d", peer.UserID),
			Tags:       peerTags,
			IsSelf:     false,
		})
	}

	b, _ := json.Marshal(devices)
	return C.CString(string(b))
}

//export StopEchoLinkNode
func StopEchoLinkNode() {
	mu.Lock()
	defer mu.Unlock()
	if tsServer != nil {
		tsServer.Close()
		tsServer = nil
		internalState = "Stopped"
	}
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

type Device struct {
	Name       string   `json:"name"`
	IpAddress  string   `json:"ipAddress"`
	IsOnline   bool     `json:"isOnline"`
	DeviceType string   `json:"deviceType"`
	Os         string   `json:"os"`
	UserID     string   `json:"userId"`
	Tags       []string `json:"tags"`
	IsSelf     bool     `json:"isSelf"`
}

//export SetAudioTargetHost
func SetAudioTargetHost(host *C.char) {
	audioTarget = C.GoString(host)
}

func startSocks5Proxy() {
	conf := &socks5.Config{
		Dial: func(ctx context.Context, network, addr string) (net.Conn, error) {
			return tsServer.Dial(ctx, network, addr)
		},
	}
	server, err := socks5.New(conf)
	if err != nil {
		log.Printf("[Socks5] Failed to create: %v", err)
		return
	}
	ln, err := net.Listen("tcp", "127.0.0.1:1055")
	if err != nil {
		log.Printf("[Socks5] Failed to listen: %v", err)
		return
	}
	log.Printf("[Socks5] Listening on 127.0.0.1:1055")
	server.Serve(ln)
}

func startPairingForwarder() {
	ln, err := tsServer.Listen("tcp", ":44444")
	if err != nil {
		log.Printf("[Pairing] Failed to listen on mesh: %v", err)
		return
	}
	for {
		conn, err := ln.Accept()
		if err != nil {
			continue
		}
		go func(c net.Conn) {
			defer c.Close()
			local, err := net.Dial("tcp", "127.0.0.1:44444")
			if err != nil {
				return
			}
			defer local.Close()
			go io.Copy(local, c)
			io.Copy(c, local)
		}(conn)
	}
}

func sftpHandler(sess ssh.Session) {
	server, err := sftp.NewServer(sess)
	if err != nil {
		log.Printf("[SFTP] Server init error: %v", err)
		return
	}
	if err := server.Serve(); err != nil && err != io.EOF {
		log.Printf("[SFTP] Server exited with error: %v", err)
	}
}

func startUnifiedAppForwarder() {
	ln, err := tsServer.Listen("tcp", ":55555")
	if err != nil {
		log.Printf("[Unified] Failed to listen on mesh port 55555: %v", err)
		return
	}
	defer ln.Close()
	
	log.Printf("[Unified] Listening on mesh port 55555, routing to 127.0.0.1:55555")

	for {
		meshConn, err := ln.Accept()
		if err != nil {
			log.Printf("[Unified] Forwarder accept error: %v", err)
			return
		}

		go func(c net.Conn) {
			defer c.Close()
			localConn, err := net.Dial("tcp", "127.0.0.1:55555")
			if err != nil {
				log.Printf("[Unified] Failed to dial local C# service: %v", err)
				return
			}
			defer localConn.Close()

			go io.Copy(localConn, c)
			io.Copy(c, localConn)
		}(meshConn)
	}
}

func startInternalSshServer() {
	log.Printf("[SSH] Starting internal server on mesh port 2222")

	publicKeyOption := ssh.PublicKeyAuth(func(ctx ssh.Context, key ssh.PublicKey) bool {
		sshMu.Lock()
		defer sshMu.Unlock()
		offeredKeyBytes := key.Marshal()
		for _, validPubStr := range tempSSHPasswords {
			parsedKey, _, _, _, err := ssh.ParseAuthorizedKey([]byte(validPubStr))
			if err == nil && bytes.Equal(parsedKey.Marshal(), offeredKeyBytes) {
				return true
			}
		}
		log.Printf("[SSH] Rejected public key login attempt")
		return false
	})

	server := &ssh.Server{
		Handler: func(s ssh.Session) {
			io.WriteString(s, "EchoLink internal SSH server (Android).\n")
		},
		SubsystemHandlers: map[string]ssh.SubsystemHandler{
			"sftp": sftpHandler,
		},
	}
	server.SetOption(publicKeyOption)

	// Use tsServer.Listen to listen on the tailnet interface specifically
	ln, err := tsServer.Listen("tcp", ":2222")
	if err != nil {
		log.Printf("[SSH] Failed to listen on mesh: %v", err)
		return
	}
	
	err = server.Serve(ln)
	if err != nil {
		log.Printf("[SSH] Server failed: %v", err)
	}
}

//export SetTempSshPassword
func SetTempSshPassword(ip *C.char, password *C.char) {
	sshMu.Lock()
	defer sshMu.Unlock()
	ipStr := C.GoString(ip)
	passStr := C.GoString(password)
	// We are repurposing this variable/function to store the Public Key string
	tempSSHPasswords[ipStr] = passStr
	log.Printf("[SSH] Authorized public key set for %s", ipStr)
}

//export RemoveTempSshPassword
func RemoveTempSshPassword(ip *C.char) {
	sshMu.Lock()
	defer sshMu.Unlock()
	ipStr := C.GoString(ip)
	delete(tempSSHPasswords, ipStr)
	log.Printf("[SSH] Authorized public key removed for %s", ipStr)
}

func main() {}
