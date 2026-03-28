using System;
using System.Text;
using System.Threading.Tasks;
using SharpHook;
using SharpHook.Native;

namespace EchoLink.Services.RemoteControl
{
    public class DesktopKeyboardSink
    {
        private readonly SimpleEventSimulator _simulator = new();

        public Task HandleKeyboardEventAsync(byte[] payload)
        {
            if (payload.Length < 1)
            {
                return Task.CompletedTask;
            }

            byte type = payload[0];

            try
            {
                switch (type)
                {
                    // Type 0: Control Key
                    case 0:
                        if (payload.Length >= 3)
                        {
                            short keyCode = BitConverter.ToInt16(payload, 1);
                            SimulateControlKeyPress(keyCode);
                        }
                        break;

                    // Type 1: Text String
                    case 1:
                        if (payload.Length > 1)
                        {
                            string text = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
                            if (!string.IsNullOrEmpty(text))
                            {
                                _simulator.SimulateTextEntry(text);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Error($"[KeyboardSink] Failed to simulate keyboard event: {ex.Message}");
            }
            
            return Task.CompletedTask;
        }

        private void SimulateControlKeyPress(short keyCode)
        {
            KeyCode simKeyCode = keyCode switch
            {
                8  => KeyCode.VcBackspace, // VK_BACK
                13 => KeyCode.VcEnter,     // VK_RETURN
                _  => KeyCode.VcUndefined
            };

            if (simKeyCode != KeyCode.VcUndefined)
            {
                _simulator.SimulateKeyPress(simKeyCode);
                _simulator.SimulateKeyRelease(simKeyCode);
            }
        }
    }
}
