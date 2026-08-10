using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cursivis.Windows.App.Helpers;

/// <summary>
/// Observes keyboard transitions only while a Hotkeys-page recorder is active.
/// A different application's RegisterHotKey registration can consume the final
/// key before WinUI raises KeyDown; this hook lets the recorder surface that
/// candidate and retain the previously active shortcut after registration
/// rejects it.
/// </summary>
internal sealed class GlobalKeyboardCaptureHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly LowLevelKeyboardProc _callback;
    private readonly Action<uint, bool> _onKeyTransition;
    private nint _hook;
    private bool _disposed;

    public GlobalKeyboardCaptureHook(Action<uint, bool> onKeyTransition)
    {
        _onKeyTransition = onKeyTransition ?? throw new ArgumentNullException(nameof(onKeyTransition));
        _callback = HookCallback;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook != nint.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(null), 0);
        if (_hook == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not capture this shortcut.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_hook != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = nint.Zero;
        }

        _disposed = true;
    }

    private nint HookCallback(int code, nint message, nint data)
    {
        if (code >= 0 &&
            message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
        {
            var keyboard = Marshal.PtrToStructure<KeyboardLowLevelHookData>(data);
            _onKeyTransition(
                keyboard.VirtualKey,
                message is WmKeyDown or WmSysKeyDown);
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private delegate nint LowLevelKeyboardProc(int code, nint message, nint data);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardLowLevelHookData
    {
        public uint VirtualKey { get; init; }

        public uint ScanCode { get; init; }

        public uint Flags { get; init; }

        public uint Time { get; init; }

        public nint ExtraInfo { get; init; }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
