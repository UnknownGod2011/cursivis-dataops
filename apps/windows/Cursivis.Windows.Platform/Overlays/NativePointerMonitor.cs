using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Overlays;

public sealed class NativePointerMonitor : IDisposable
{
    private const int LowLevelMouseHook = 14;
    private const int LeftButtonDown = 0x0201;
    private const int RightButtonDown = 0x0204;
    private const int MiddleButtonDown = 0x0207;
    private const int ExtraButtonDown = 0x020B;
    private readonly LowLevelMouseProcedure _procedure;
    private nint _hook;
    private bool _disposed;

    public NativePointerMonitor()
    {
        _procedure = HookProcedure;
    }

    public event EventHandler<NativePointerPressedEventArgs>? PointerPressed;

    public bool IsRunning => _hook != nint.Zero;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != nint.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(
            LowLevelMouseHook,
            _procedure,
            GetModuleHandle(null),
            0);
        if (_hook == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The transient-overlay pointer monitor could not start.");
        }
    }

    public void Stop()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        _ = UnhookWindowsHookEx(_hook);
        _hook = nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private nint HookProcedure(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && IsButtonDown((int)wParam))
        {
            MouseHookData data = Marshal.PtrToStructure<MouseHookData>(lParam);
            PointerPressed?.Invoke(
                this,
                new NativePointerPressedEventArgs(new OverlayPoint(data.Point.X, data.Point.Y)));
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsButtonDown(int message) => message is
        LeftButtonDown or RightButtonDown or MiddleButtonDown or ExtraButtonDown;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private delegate nint LowLevelMouseProcedure(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelMouseProcedure procedure,
        nint moduleHandle,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}

public sealed class NativePointerPressedEventArgs(OverlayPoint position) : EventArgs
{
    public OverlayPoint Position { get; } = position;
}
