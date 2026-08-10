using Cursivis.Application.Context;
using Cursivis.Domain.Context;
using Cursivis.Windows.Platform.Foreground;

namespace Cursivis.Windows.Platform.Selection;

public sealed class WindowsTextInsertionService(
    IForegroundWindowIdentityProvider foreground) : ITextInsertionService
{
    private readonly IForegroundWindowIdentityProvider _foreground = foreground
        ?? throw new ArgumentNullException(nameof(foreground));

    public Task<TextInsertionResult> InsertAsync(
        ContextSnapshot expectedContext,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new TextInsertionResult(
                TextInsertionStatus.Cancelled,
                "Text insertion was cancelled."));
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
            return Task.FromResult(new TextInsertionResult(
                TextInsertionStatus.StaleTarget,
                "The original application is no longer in the foreground."));
        }

        FocusedTextPasteResult paste = WindowsFocusedTextPaste.TryPaste(
            current.WindowHandle,
            text);
        return Task.FromResult(new TextInsertionResult(
            paste.Status switch
            {
                FocusedTextPasteStatus.Pasted => TextInsertionStatus.Inserted,
                FocusedTextPasteStatus.Unsupported => TextInsertionStatus.UnsupportedTarget,
                _ => TextInsertionStatus.Failed,
            },
            paste.SafeDetail));
    }
}
