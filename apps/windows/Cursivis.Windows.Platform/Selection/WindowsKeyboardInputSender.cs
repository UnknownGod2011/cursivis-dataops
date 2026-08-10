using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Selection;

internal interface IWindowsKeyboardInputSender
{
    bool TrySendChord(ushort modifierVirtualKey, ushort virtualKey);
}

internal sealed class WindowsKeyboardInputSender : IWindowsKeyboardInputSender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    internal static int NativeInputSize => Marshal.SizeOf<Input>();

    public bool TrySendChord(ushort modifierVirtualKey, ushort virtualKey)
    {
        Input[] inputs =
        [
            CreateKey(modifierVirtualKey, keyUp: false),
            CreateKey(virtualKey, keyUp: false),
            CreateKey(virtualKey, keyUp: true),
            CreateKey(modifierVirtualKey, keyUp: true),
        ];
        return SendInput((uint)inputs.Length, inputs, NativeInputSize) == inputs.Length;
    }

    private static Input CreateKey(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);
}
