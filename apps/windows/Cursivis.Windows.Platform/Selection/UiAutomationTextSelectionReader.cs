using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Cursivis.Application.Context;
using Cursivis.Domain.Context;

namespace Cursivis.Windows.Platform.Selection;

public sealed class UiAutomationTextSelectionReader : ITextSelectionReader
{
    // A short UIA budget keeps the fallback path responsive for Chromium,
    // Electron, PDF, and custom controls whose providers do not expose a text
    // range. The abandoned read releases its gate when it completes.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(180);
    private readonly SemaphoreSlim _singleRead = new(1, 1);

    public async Task<TextSelectionReadResult> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!await _singleRead.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return TextSelectionReadResult.Failed(
                TextSelectionReadStatus.Unavailable,
                ContextSource.UserInterfaceAutomation,
                "A previous UI Automation read is still completing.");
        }

        Task<TextSelectionReadResult> readTask = Task.Run(ReadCore, CancellationToken.None);
        _ = readTask.ContinueWith(
            static (_, state) => ((SemaphoreSlim)state!).Release(),
            _singleRead,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            return await readTask.WaitAsync(ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return TextSelectionReadResult.Failed(
                TextSelectionReadStatus.TimedOut,
                ContextSource.UserInterfaceAutomation,
                "UI Automation selection detection timed out.");
        }
        catch (OperationCanceledException)
        {
            return TextSelectionReadResult.Failed(
                TextSelectionReadStatus.Cancelled,
                ContextSource.UserInterfaceAutomation,
                "Selection detection was cancelled.");
        }
    }

    private static TextSelectionReadResult ReadCore()
    {
        try
        {
            AutomationElement focused = AutomationElement.FocusedElement;
            // Only trust the focused UIA element. Chromium can expose a
            // selected omnibox URL beside the actual page selection; walking
            // every foreground descendant makes that arbitrary browser chrome
            // look like user context. The protected clipboard reader handles
            // providers that do not expose the focused selection safely.
            string? selectedText = TryReadSelection(focused);

            return string.IsNullOrWhiteSpace(selectedText)
                ? NoSelection("The foreground application has no selected text range.")
                : TextSelectionReadResult.Captured(
                    selectedText,
                    ContextSource.UserInterfaceAutomation);
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException or InvalidOperationException or
            COMException or UnauthorizedAccessException)
        {
            return TextSelectionReadResult.Failed(
                TextSelectionReadStatus.Unavailable,
                ContextSource.UserInterfaceAutomation,
                "UI Automation could not read the focused control.");
        }
    }

    private static string? TryReadSelection(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object? patternObject) ||
            patternObject is not TextPattern textPattern ||
            textPattern.SupportedTextSelection == SupportedTextSelection.None)
        {
            return null;
        }

        TextPatternRange[] ranges = textPattern.GetSelection();
        var selected = new List<string>(ranges.Length);
        foreach (TextPatternRange range in ranges)
        {
            string value = range.GetText(ContextTriggerService.MaximumSelectionCharacters + 1);
            if (!string.IsNullOrWhiteSpace(value))
            {
                selected.Add(value);
            }
        }

        return selected.Count == 0
            ? null
            : string.Join(Environment.NewLine, selected);
    }

    private static TextSelectionReadResult NoSelection(string detail) =>
        TextSelectionReadResult.Failed(
            TextSelectionReadStatus.NoSelection,
            ContextSource.UserInterfaceAutomation,
            detail);
}
