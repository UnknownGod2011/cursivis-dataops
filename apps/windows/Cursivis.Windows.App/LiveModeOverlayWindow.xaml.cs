using Cursivis.Application.Realtime;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Windows.Foundation;

namespace Cursivis.Windows.App;

public sealed partial class LiveModeOverlayWindow : Window
{
    private const int LogicalWidth = 388;
    private const int LogicalHeight = 144;
    private const int CornerRadius = 22;
    private readonly TransparentWindowBackdrop _transparentBackdrop;
    private readonly NativeOverlayWindow _overlay;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly OverlayPlacementStore _placementStore;
    private OverlayPoint? _savedPlacement;
    private bool _userPlaced;

    public LiveModeOverlayWindow()
    {
        InitializeComponent();
        Title = "Cursivis Live Mode";
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _transparentBackdrop = new TransparentWindowBackdrop(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragHeader);

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _overlay = new NativeOverlayWindow(handle, noActivate: true);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClientPointerSource.ExitedMoveSize += OnNativeMoveSizeExited;
        _placementStore = new OverlayPlacementStore(
            CursivisStoragePaths.ForCurrentUser().LiveModePlacementFile);
        _savedPlacement = _placementStore.TryLoad();
        _userPlaced = _savedPlacement is not null;
        OverlayRoot.Loaded += OnOverlayRootLoaded;
        OverlayRoot.SizeChanged += OnOverlayRootSizeChanged;
        Closed += OnClosed;
    }

    public event EventHandler? StopRequested;

    public bool IsVisible => _overlay.IsVisible;

    public void ShowSnapshot(LiveModeSnapshot snapshot, OverlayRectangle? orbBounds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StatusText.Text = snapshot.Status;
        UserTranscriptText.Text = string.IsNullOrWhiteSpace(snapshot.UserTranscript)
            ? UserFallback(snapshot.State)
            : snapshot.UserTranscript;
        AssistantTranscriptText.Text = snapshot.State == LiveModeState.Error
            ? snapshot.SafeError ?? "Live Mode unavailable."
            : string.IsNullOrWhiteSpace(snapshot.AssistantTranscript)
                ? AssistantFallback(snapshot)
                : snapshot.AssistantTranscript;
        AudioLevelFill.Width = 72 * Math.Clamp(snapshot.AudioLevel, 0, 1);
        AutomationProperties.SetName(
            OverlayRoot,
            $"Cursivis Live Mode. {snapshot.Status}. {UserTranscriptText.Text}. {AssistantTranscriptText.Text}");

        if (!_overlay.IsVisible)
        {
            OverlayRectangle bounds = _userPlaced && _savedPlacement is OverlayPoint placement
                ? NativeWindowPositioner.GetRestoredPlacement(
                    _overlay.WindowHandle,
                    LogicalWidth,
                    LogicalHeight,
                    placement)
                : orbBounds is OverlayRectangle orb
                    ? PlaceBesideOrb(orb)
                    : NativeWindowPositioner.GetNearCursorPlacement(
                        _overlay.WindowHandle,
                        LogicalWidth,
                        LogicalHeight);
            AppWindow.Show(activateWindow: false);
            _overlay.Show(bounds, topmost: true, noActivate: true, CornerRadius);
            _ = DispatcherQueue.TryEnqueue(_overlay.EnforceBorderlessStyle);
        }
    }

    private OverlayRectangle PlaceBesideOrb(OverlayRectangle orbBounds)
    {
        const int gap = 12;
        OverlayRectangle measured = NativeWindowPositioner.GetRestoredPlacement(
            _overlay.WindowHandle,
            LogicalWidth,
            LogicalHeight,
            new OverlayPoint(orbBounds.Right + gap, orbBounds.Y));
        int centeredY = orbBounds.Y + ((orbBounds.Height - measured.Height) / 2);
        OverlayRectangle right = NativeWindowPositioner.GetRestoredPlacement(
            _overlay.WindowHandle,
            LogicalWidth,
            LogicalHeight,
            new OverlayPoint(orbBounds.Right + gap, centeredY));
        if (right.X >= orbBounds.Right)
        {
            return right;
        }

        return NativeWindowPositioner.GetRestoredPlacement(
            _overlay.WindowHandle,
            LogicalWidth,
            LogicalHeight,
            new OverlayPoint(orbBounds.X - measured.Width - gap, centeredY));
    }

    public void SetRequestedTheme(ElementTheme theme) => OverlayRoot.RequestedTheme = theme;

    public void Hide() => _overlay.Hide();

    private static string UserFallback(LiveModeState state) => state switch
    {
        LiveModeState.Connecting => "Preparing microphone…",
        LiveModeState.Listening => "Listening…",
        LiveModeState.UserSpeaking => "Listening…",
        _ => "—",
    };

    private static string AssistantFallback(LiveModeSnapshot snapshot) => snapshot.State switch
    {
        LiveModeState.Connecting => "Connecting to OpenAI Realtime…",
        LiveModeState.Thinking => "Thinking…",
        LiveModeState.ExecutingTool => snapshot.Status,
        LiveModeState.Error => snapshot.SafeError ?? "Live Mode unavailable.",
        _ => "Ready",
    };

    private void OnStopClicked(object sender, RoutedEventArgs args) =>
        StopRequested?.Invoke(this, EventArgs.Empty);

    private void OnOverlayRootSizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateInteractiveHeaderRegions();

    private void OnOverlayRootLoaded(object sender, RoutedEventArgs args) =>
        UpdateInteractiveHeaderRegions();

    private void UpdateInteractiveHeaderRegions()
    {
        if (EndButton.ActualWidth <= 0 || EndButton.ActualHeight <= 0)
        {
            return;
        }

        Point origin = EndButton.TransformToVisual(OverlayRoot).TransformPoint(new Point(0, 0));
        double scale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.Passthrough,
            [new global::Windows.Graphics.RectInt32(
                Math.Max(0, (int)Math.Floor(origin.X * scale)),
                Math.Max(0, (int)Math.Floor(origin.Y * scale)),
                Math.Max(1, (int)Math.Ceiling(EndButton.ActualWidth * scale)),
                Math.Max(1, (int)Math.Ceiling(EndButton.ActualHeight * scale)))]);
    }

    private async void OnNativeMoveSizeExited(
        InputNonClientPointerSource sender,
        ExitedMoveSizeEventArgs args)
    {
        try
        {
            OverlayRectangle clamped = NativeWindowPositioner.ClampCurrentPlacement(
                _overlay.WindowHandle,
                _overlay.GetBounds());
            _overlay.Show(clamped, topmost: true, noActivate: _overlay.IsNoActivate, CornerRadius);
            _savedPlacement = new OverlayPoint(clamped.X, clamped.Y);
            _userPlaced = true;
            await _placementStore.SaveAsync(_savedPlacement.Value);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        OverlayRoot.Loaded -= OnOverlayRootLoaded;
        OverlayRoot.SizeChanged -= OnOverlayRootSizeChanged;
        _nonClientPointerSource.ExitedMoveSize -= OnNativeMoveSizeExited;
        _nonClientPointerSource.ClearAllRegionRects();
        Closed -= OnClosed;
        _transparentBackdrop.Dispose();
        _overlay.Dispose();
    }
}
