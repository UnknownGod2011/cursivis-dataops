using Cursivis.Application.Context;
using Cursivis.Application.Realtime;
using Cursivis.Windows.Platform.Foreground;
using Cursivis.Windows.Platform.Hotkeys;

namespace Cursivis.Windows.App;

internal sealed class LiveModeContextProvider(
    IHotkeyInputSettler inputSettler,
    ISelectionCaptureService selectionCapture,
    IForegroundWindowIdentityProvider foreground,
    IWindowThreadDispatcher uiDispatcher) : ILiveModeContextProvider
{
    private static readonly TimeSpan ContextLifetime = TimeSpan.FromMinutes(5);
    private readonly IHotkeyInputSettler _inputSettler = inputSettler
        ?? throw new ArgumentNullException(nameof(inputSettler));
    private readonly ISelectionCaptureService _selectionCapture = selectionCapture
        ?? throw new ArgumentNullException(nameof(selectionCapture));
    private readonly IForegroundWindowIdentityProvider _foreground = foreground
        ?? throw new ArgumentNullException(nameof(foreground));
    private readonly IWindowThreadDispatcher _uiDispatcher = uiDispatcher
        ?? throw new ArgumentNullException(nameof(uiDispatcher));

    public Task<LiveModeContext> CaptureAsync(CancellationToken cancellationToken = default) =>
        // Protected clipboard and UIA reads require the WinUI STA owner thread.
        // Live Mode session startup continues on a worker after the first await.
        _uiDispatcher.InvokeAsync(async () =>
        {
            await _inputSettler.WaitForModifiersReleasedAsync(cancellationToken);
            ForegroundWindowIdentity? active = _foreground.GetCurrent();
            SelectionCaptureResult capture = await _selectionCapture.CaptureAsync(
                ContextLifetime,
                cancellationToken);

            return capture.Status == SelectionCaptureStatus.Captured && capture.Context is not null
                ? new LiveModeContext(
                    capture.Context.Text,
                    capture.Context.Fingerprint.Value,
                    capture.Context.Target.ApplicationId,
                    active?.WindowTitle,
                    capture.Context)
                : new LiveModeContext(
                    null,
                    null,
                    active?.ProcessName,
                    active?.WindowTitle,
                    null);
        }, cancellationToken);
}
