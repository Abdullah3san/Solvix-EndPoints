namespace Solvix.Agent.Windows.Remote;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Remote;

/// <summary>Applies the technician's mouse and keyboard to the user's desktop (<c>SendInput</c>), when control was granted.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class SendInputInjector : IInputInjector
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;
    private const uint MouseAbsolute = 0x8000;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUp = 0x0002;
    private const uint KeyUnicode = 0x0004;

    public void Inject(ScreenInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch (input.Type)
        {
            case ScreenInputEvent.Move:
                SendMouse(MouseMove | MouseAbsolute, 0, input);
                break;
            case ScreenInputEvent.Down:
            case ScreenInputEvent.Up:
                var down = input.Type == ScreenInputEvent.Down;
                var flag = (input.Button?.ToLowerInvariant()) switch
                {
                    "right" => down ? MouseRightDown : MouseRightUp,
                    "middle" => down ? MouseMiddleDown : MouseMiddleUp,
                    _ => down ? MouseLeftDown : MouseLeftUp,
                };
                SendMouse(MouseMove | MouseAbsolute | flag, 0, input);
                break;
            case ScreenInputEvent.Wheel:
                SendMouse(MouseWheel, unchecked((uint)(Math.Clamp(input.Delta, -20, 20) * 120)), input);
                break;
            case ScreenInputEvent.Key:
                if (KeyMap.TryGetVirtualKey(input.Name, out var vk, out var extended))
                {
                    SendKey(vk, 0, (input.Pressed ? 0u : KeyUp) | (extended ? KeyExtended : 0));
                }

                break;
            case ScreenInputEvent.Text:
                foreach (var c in (input.Characters ?? string.Empty).Take(64))
                {
                    SendKey(0, c, KeyUnicode);
                    SendKey(0, c, KeyUnicode | KeyUp);
                }

                break;
        }
    }

    private static void SendMouse(uint flags, uint data, ScreenInputEvent input)
    {
        var x = (int)(Math.Clamp(input.X, 0.0, 1.0) * 65535);
        var y = (int)(Math.Clamp(input.Y, 0.0, 1.0) * 65535);
        var item = new Input { Type = InputMouse, Union = new InputUnion { Mouse = new MouseInput { Dx = x, Dy = y, MouseData = data, Flags = flags } } };
        Send(item);
    }

    private static void SendKey(ushort virtualKey, ushort scan, uint flags)
    {
        var item = new Input { Type = InputKeyboard, Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, ScanCode = scan, Flags = flags } } };
        Send(item);
    }

    private static void Send(Input item)
    {
        Input[] items = [item];
        SendInput((uint)items.Length, items, Marshal.SizeOf<Input>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);
}
