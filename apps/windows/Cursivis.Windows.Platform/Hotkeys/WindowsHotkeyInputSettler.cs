using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Hotkeys;

public interface IHotkeyInputSettler
{
    Task WaitForModifiersReleasedAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Prevents selection capture from racing the still-held global-hotkey modifiers.
/// </summary>
public sealed class WindowsHotkeyInputSettler : IHotkeyInputSettler
{
    private const int VirtualKeyShift = 0x10;
    private const int VirtualKeyControl = 0x11;
    private const int VirtualKeyMenu = 0x12;
    private const int VirtualKeyLeftWindows = 0x5B;
    private const int VirtualKeyRightWindows = 0x5C;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan KeyUpDispatchGrace = TimeSpan.FromMilliseconds(24);

    public async Task WaitForModifiersReleasedAsync(
        CancellationToken cancellationToken = default)
    {
        while (AnyModifierPressed())
        {
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(true);
        }

        // GetAsyncKeyState reflects physical state before every target message
        // queue has necessarily consumed the final key-up. Give that key-up one
        // short dispatch window before injecting Ctrl+C; otherwise a synthetic
        // trigger can race the copy chord and deliver a literal 'c'.
        await Task.Delay(KeyUpDispatchGrace, cancellationToken).ConfigureAwait(true);
    }

    private static bool AnyModifierPressed() =>
        IsPressed(VirtualKeyShift) ||
        IsPressed(VirtualKeyControl) ||
        IsPressed(VirtualKeyMenu) ||
        IsPressed(VirtualKeyLeftWindows) ||
        IsPressed(VirtualKeyRightWindows);

    private static bool IsPressed(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
