using System.Diagnostics;
using Cursivis.Application.Context;
using Cursivis.Domain.Context;
using Cursivis.Windows.Platform.Foreground;

namespace Cursivis.Windows.Platform.Selection;

public sealed class LayeredSelectionCaptureService : ISelectionCaptureService
{
    private readonly IForegroundWindowIdentityProvider _foreground;
    private readonly IReadOnlyList<ITextSelectionReader> _readers;
    private readonly TimeProvider _timeProvider;

    public LayeredSelectionCaptureService(
        IForegroundWindowIdentityProvider foreground,
        IEnumerable<ITextSelectionReader> readers,
        TimeProvider? timeProvider = null)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers.ToArray();
        if (_readers.Count == 0)
        {
            throw new ArgumentException("At least one selection reader is required.", nameof(readers));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SelectionCaptureResult> CaptureAsync(
        TimeSpan contextLifetime,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        ForegroundWindowIdentity? initial = _foreground.GetCurrent();
        if (initial is null || initial.WindowHandle == nint.Zero)
        {
            return SelectionCaptureResult.Failed(
                SelectionCaptureStatus.Unavailable,
                Stopwatch.GetElapsedTime(started),
                "The foreground application could not be identified.");
        }

        foreach (ITextSelectionReader reader in _readers)
        {
            // Clipboard fallback must remain on the caller's STA. WinUI invokes this
            // service from its UI thread, where OLE clipboard and WinRT clipboard
            // operations are initialized and safe to use.
            TextSelectionReadResult read = reader is IForegroundBoundTextSelectionReader boundReader
                ? await boundReader.ReadForWindowAsync(initial.WindowHandle, cancellationToken)
                : await reader.ReadAsync(cancellationToken);
            if (read.Status == TextSelectionReadStatus.Cancelled)
            {
                return SelectionCaptureResult.Failed(
                    SelectionCaptureStatus.Cancelled,
                    Stopwatch.GetElapsedTime(started),
                    read.SafeDetail);
            }

            if (read.Status != TextSelectionReadStatus.Captured || string.IsNullOrWhiteSpace(read.Text))
            {
                continue;
            }

            string text = read.Text.Trim();
            if (ShouldVerifyBrowserSelectionWithClipboard(initial, read, text))
            {
                // A focused Chromium omnibox can expose a URL as a UIA
                // selection even while the visible document selection is a
                // word or phrase. Do not treat that browser-chrome value as
                // context; continue to the protected Ctrl+C fallback, which
                // is bound to the original foreground window and restores the
                // user's clipboard after the read.
                continue;
            }

            ForegroundWindowIdentity? current = _foreground.GetCurrent();
            if (!IsSameTarget(initial, current))
            {
                return SelectionCaptureResult.Failed(
                    SelectionCaptureStatus.ForegroundChanged,
                    Stopwatch.GetElapsedTime(started),
                    "The foreground application changed while Cursivis captured the selection.");
            }

            if (text.Length > ContextTriggerService.MaximumSelectionCharacters)
            {
                return SelectionCaptureResult.Failed(
                    SelectionCaptureStatus.Unavailable,
                    Stopwatch.GetElapsedTime(started),
                    "The selected text exceeds the current 50,000-character limit.");
            }

            string applicationId = string.IsNullOrWhiteSpace(initial.ProcessName)
                ? $"pid-{initial.ProcessId}"
                : initial.ProcessName;
            var target = new TargetIdentity(
                applicationId,
                $"{initial.WindowHandle.ToInt64():X}");
            ContextSnapshot context = ContextSnapshot.FromText(
                TextContextKindClassifier.Classify(text),
                read.Source,
                target,
                text,
                _timeProvider.GetUtcNow(),
                contextLifetime);
            return SelectionCaptureResult.Captured(context, Stopwatch.GetElapsedTime(started));
        }

        return SelectionCaptureResult.Failed(
            SelectionCaptureStatus.NoSelection,
            Stopwatch.GetElapsedTime(started),
            "No selected text was detected.");
    }

    private static bool IsSameTarget(
        ForegroundWindowIdentity expected,
        ForegroundWindowIdentity? actual) =>
        actual is not null &&
        expected.ProcessId == actual.ProcessId &&
        expected.WindowHandle == actual.WindowHandle;

    private static bool ShouldVerifyBrowserSelectionWithClipboard(
        ForegroundWindowIdentity foreground,
        TextSelectionReadResult read,
        string text)
    {
        if (read.Source != ContextSource.UserInterfaceAutomation ||
            !IsChromiumFamilyProcess(foreground.ProcessName) ||
            !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        // Uri.UriSchemeHttp/Https are not compile-time constants, so use
        // ordinal comparisons rather than constant-pattern matching.
        return IsHttpOrHttpsScheme(uri.Scheme);
    }

    private static bool IsHttpOrHttpsScheme(string scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

    private static bool IsChromiumFamilyProcess(string? processName) =>
        processName is not null &&
        (processName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("vivaldi", StringComparison.OrdinalIgnoreCase) ||
         processName.Equals("electron", StringComparison.OrdinalIgnoreCase));
}
