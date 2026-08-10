using Cursivis.Application.Context;
using Cursivis.Domain.Context;
using Cursivis.Windows.Platform.Foreground;

namespace Cursivis.Windows.Platform.Selection;

public sealed class WindowsSelectionReplacementService : ISelectionReplacementService
{
    private readonly IForegroundWindowIdentityProvider _foreground;
    private readonly IReadOnlyList<ITextSelectionReader> _selectionReaders;

    public WindowsSelectionReplacementService(
        IForegroundWindowIdentityProvider foreground,
        IEnumerable<ITextSelectionReader> selectionReaders)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        ArgumentNullException.ThrowIfNull(selectionReaders);
        _selectionReaders = selectionReaders.ToArray();
        if (_selectionReaders.Count == 0)
        {
            throw new ArgumentException(
                "At least one selection reader is required for safe replacement.",
                nameof(selectionReaders));
        }
    }

    public async Task<TextReplacementResult> ReplaceAsync(
        ContextSnapshot expectedContext,
        string replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacement);
        cancellationToken.ThrowIfCancellationRequested();

        ForegroundWindowIdentity? current = _foreground.GetCurrent();
        if (current is null ||
            !long.TryParse(expectedContext.Target.WindowId, System.Globalization.NumberStyles.HexNumber, null, out long expectedHandle) ||
            current.WindowHandle != new nint(expectedHandle))
        {
            return new TextReplacementResult(
                TextReplacementStatus.StaleTarget,
                "The original application is no longer in the foreground.");
        }

        TextSelectionReadResult currentSelection = await ReadCurrentSelectionAsync(cancellationToken);
        if (currentSelection.Status == TextSelectionReadStatus.Cancelled)
        {
            return new TextReplacementResult(
                TextReplacementStatus.Cancelled,
                "Selection replacement was cancelled.");
        }

        if (currentSelection.Status != TextSelectionReadStatus.Captured ||
            string.IsNullOrWhiteSpace(currentSelection.Text))
        {
            return new TextReplacementResult(
                TextReplacementStatus.UnsupportedTarget,
                "The original selection could not be revalidated.");
        }

        if (!string.Equals(
                expectedContext.Text?.Trim(),
                currentSelection.Text.Trim(),
                StringComparison.Ordinal))
        {
            return new TextReplacementResult(
                TextReplacementStatus.StaleTarget,
                "The selected text changed after it was captured.");
        }

        FocusedTextPasteResult paste = WindowsFocusedTextPaste.TryPaste(
            current.WindowHandle,
            replacement);
        return new TextReplacementResult(
            paste.Status switch
            {
                FocusedTextPasteStatus.Pasted => TextReplacementStatus.Replaced,
                FocusedTextPasteStatus.Unsupported => TextReplacementStatus.UnsupportedTarget,
                _ => TextReplacementStatus.Failed,
            },
            paste.SafeDetail);
    }

    private async Task<TextSelectionReadResult> ReadCurrentSelectionAsync(
        CancellationToken cancellationToken)
    {
        TextSelectionReadResult? last = null;
        foreach (ITextSelectionReader reader in _selectionReaders)
        {
            last = await reader.ReadAsync(cancellationToken);
            if (last.Status is TextSelectionReadStatus.Captured or TextSelectionReadStatus.Cancelled)
            {
                return last;
            }
        }

        return last ?? TextSelectionReadResult.Failed(
            TextSelectionReadStatus.Unavailable,
            ContextSource.DirectInput,
            "No selection reader was available.");
    }

}
