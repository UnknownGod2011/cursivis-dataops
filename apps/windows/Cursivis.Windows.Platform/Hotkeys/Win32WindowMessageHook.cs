using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Hotkeys;

public sealed class HotkeyPressedEventArgs(int registrationId) : EventArgs
{
    public int RegistrationId { get; } = registrationId;
}

/// <summary>
/// Subclasses an existing same-thread Win32 window and surfaces only WM_HOTKEY.
/// The delegate is retained for the complete native subclass lifetime.
/// </summary>
public sealed class Win32WindowMessageHook : IDisposable
{
    private const uint WmHotkey = 0x0312;
    private const uint WmNcDestroy = 0x0082;
    private const nuint SubclassId = 0x43565253; // "CVRS"

    private readonly nint _windowHandle;
    private readonly SubclassProcedure _procedure;
    private bool _attached;

    public Win32WindowMessageHook(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _procedure = WindowProcedure;
        _attached = SetWindowSubclass(_windowHandle, _procedure, SubclassId, 0);
        if (!_attached)
        {
            throw new InvalidOperationException("The Cursivis window message hook could not be installed.");
        }
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public void Dispose()
    {
        if (_attached)
        {
            _ = RemoveWindowSubclass(_windowHandle, _procedure, SubclassId);
            _attached = false;
        }

        GC.SuppressFinalize(this);
    }

    private nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmHotkey)
        {
            HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(unchecked((int)wParam)));
        }
        else if (message == WmNcDestroy)
        {
            _ = RemoveWindowSubclass(windowHandle, _procedure, subclassId);
            _attached = false;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);
}
