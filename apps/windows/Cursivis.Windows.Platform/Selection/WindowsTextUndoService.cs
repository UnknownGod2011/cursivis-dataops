using Cursivis.Application.Context;
using Cursivis.Domain.Context;
using Cursivis.Windows.Platform.Foreground;

namespace Cursivis.Windows.Platform.Selection;

public sealed class WindowsTextUndoService : ITextUndoService
{
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyZ = 0x5A;
    private readonly IForegroundWindowIdentityProvider _foreground;
    private readonly IWindowsKeyboardInputSender _keyboardInput;

    public WindowsTextUndoService(IForegroundWindowIdentityProvider foreground)
        : this(foreground, new WindowsKeyboardInputSender())
    {
    }

    internal WindowsTextUndoService(
        IForegroundWindowIdentityProvider foreground,
        IWindowsKeyboardInputSender keyboardInput)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _keyboardInput = keyboardInput ?? throw new ArgumentNullException(nameof(keyboardInput));
    }

    public Task<TextUndoResult> UndoAsync(
        ContextSnapshot expectedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new TextUndoResult(
                TextUndoStatus.Cancelled,
                "Text undo was cancelled."));
        }

        ForegroundWindowIdentity? current = _foreground.GetCurrent();
        if (current is null ||
            !long.TryParse(
                expectedContext.Target.WindowId,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out long expectedHandle) ||
            current.WindowHandle != new nint(expectedHandle))
        {
            return Task.FromResult(new TextUndoResult(
                TextUndoStatus.StaleTarget,
                "The original application is no longer in the foreground."));
        }

        return Task.FromResult(_keyboardInput.TrySendChord(VirtualKeyControl, VirtualKeyZ)
            ? new TextUndoResult(TextUndoStatus.Undone, "The previous text replacement was undone.")
            : new TextUndoResult(TextUndoStatus.Failed, "Windows could not send the undo shortcut."));
    }
}
