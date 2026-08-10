using Cursivis.Domain.Context;

namespace Cursivis.Windows.Platform.Selection;

public enum TextSelectionReadStatus
{
    Captured,
    NoSelection,
    TimedOut,
    Cancelled,
    Unavailable,
}

public sealed record TextSelectionReadResult(
    TextSelectionReadStatus Status,
    string? Text,
    ContextSource Source,
    string SafeDetail)
{
    public static TextSelectionReadResult Captured(string text, ContextSource source) =>
        new(TextSelectionReadStatus.Captured, text, source, string.Empty);

    public static TextSelectionReadResult Failed(
        TextSelectionReadStatus status,
        ContextSource source,
        string safeDetail)
    {
        if (status == TextSelectionReadStatus.Captured)
        {
            throw new ArgumentException("Captured text is required for a successful read.", nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(safeDetail);
        return new TextSelectionReadResult(status, null, source, safeDetail.Trim());
    }
}

public interface ITextSelectionReader
{
    Task<TextSelectionReadResult> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A reader that can bind a capture to the foreground HWND observed before the
/// Cursivis overlay appears. Clipboard readers use this to avoid sending a copy
/// chord to a window that changed during capture.
/// </summary>
public interface IForegroundBoundTextSelectionReader : ITextSelectionReader
{
    Task<TextSelectionReadResult> ReadForWindowAsync(
        nint sourceWindowHandle,
        CancellationToken cancellationToken = default);
}
