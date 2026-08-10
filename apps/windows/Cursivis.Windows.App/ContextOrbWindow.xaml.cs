using System.Numerics;
using Cursivis.Application.Context;
using Cursivis.Application.Presentation;
using Cursivis.Domain.Context;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace Cursivis.Windows.App;

public sealed partial class ContextOrbWindow : Window
{
    private const int LogicalWidth = 320;
    private const int LogicalHeight = 320;
    private const int CornerRadius = 1;
    private readonly TransparentWindowBackdrop _transparentBackdrop;
    private readonly NativeOverlayWindow _overlay;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly OverlayPlacementStore _placementStore;
    private OverlayPoint? _placementPoint;
    private bool _userPlaced;
    private GuidedOption[] _guidedOptions = [];
    private bool _awaitingBorderlessFrame;
    private int _borderlessFrames;
    private bool _liveTranscriptPanelVisible = true;

    public ContextOrbWindow()
    {
        InitializeComponent();
        Title = ResourceText.Get("ContextOrbWindowTitle");
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _transparentBackdrop = new TransparentWindowBackdrop(this);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        ExtendsContentIntoTitleBar = true;
        // The complete core orb is the move surface. StatusText was too small
        // to provide a reliable drag target and made placement feel broken.
        SetTitleBar(DragSurface);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClientPointerSource.ExitedMoveSize += OnNativeMoveSizeExited;

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _overlay = new NativeOverlayWindow(handle, noActivate: true);
        _placementStore = new OverlayPlacementStore(
            CursivisStoragePaths.ForCurrentUser().OverlayPlacementFile);
        _placementPoint = _placementStore.TryLoad();
        _userPlaced = _placementPoint is not null;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;
        ApplyState(OrbPresentationState.Idle, "Ready", "Drag to place · microphone available");
    }

    public event EventHandler? CancelRequested;

    public event EventHandler? MicrophoneRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler<LiveTranscriptVisibilityRequestedEventArgs>? LiveTranscriptVisibilityRequested;

    public event EventHandler<GuidedOperationRequestedEventArgs>? GuidedOperationRequested;

    public bool IsVisible => _overlay.IsVisible;

    public OverlayRectangle OverlayBounds => _overlay.GetBounds();

    public void ShowState(OrbPresentationState state, string status, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        SetGuidedPanelVisible(false);
        SetLiveTranscriptToggleVisible(false, false);
        DetailText.Visibility = Visibility.Collapsed;
        ApplyState(state, status, detail);
        ShowWithoutActivation();
    }

    public void ShowLiveState(
        OrbPresentationState state,
        string status,
        string detail,
        float audioLevel,
        bool transcriptPanelVisible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        SetGuidedPanelVisible(false);
        SetLiveTranscriptToggleVisible(true, transcriptPanelVisible);
        DetailText.Visibility = Visibility.Visible;
        ApplyState(state, status, detail, animateLiveState: true);
        // Live Mode alone gets a compact, restrained halo. Ordinary workflow
        // states never expose a ring, pulse, or large transparent host region.
        StateRing.Opacity = Math.Clamp(0.11 + (Math.Clamp(audioLevel, 0, 1) * 0.08), 0.11, 0.19);
        if (!_overlay.IsVisible)
        {
            ShowWithoutActivation();
        }
    }

    public void ShowDictationState(
        OrbPresentationState state,
        string status,
        string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        SetGuidedPanelVisible(false);
        SetLiveTranscriptToggleVisible(false, false);
        DetailText.Visibility = Visibility.Visible;
        ApplyState(state, status, detail);
        // Smart Dictation is a bounded one-shot workflow. Keep the original
        // static orb treatment; animated audio glow is exclusive to Live Mode.
        StateRing.Opacity = 0.16;
        if (!_overlay.IsVisible)
        {
            ShowWithoutActivation();
        }
    }

    public void ShowGuidedOptions(IReadOnlyList<GuidedOption> dynamicOptions)
    {
        ArgumentNullException.ThrowIfNull(dynamicOptions);
        BuildGuidedOptions(dynamicOptions);
        SetGuidedPanelVisible(true);
        ApplyState(
            OrbPresentationState.Guiding,
            ResourceText.Get("ContextOrbGuidedStatus"),
            ResourceText.Get("ContextOrbGuidedDetail"));
        ShowWithoutActivation();
        _ = _overlay.ActivateForInteraction();
        FocusFirstGuidedControl();
    }

    public void SetRequestedTheme(ElementTheme theme) => OverlayRoot.RequestedTheme = theme;

    public void ResetRememberedPlacement()
    {
        _placementStore.Reset();
        _placementPoint = null;
        _userPlaced = false;
        if (_overlay.IsVisible)
        {
            OverlayRectangle bounds = NativeWindowPositioner.GetNearCursorPlacement(
                _overlay.WindowHandle,
                LogicalWidth,
                LogicalHeight);
            _overlay.Show(bounds, topmost: true, noActivate: true, CornerRadius);
        }
    }

    public void Hide()
    {
        SetGuidedPanelVisible(false);
        SetLiveTranscriptToggleVisible(false, false);
        DetailText.Visibility = Visibility.Collapsed;
        StateRing.Visibility = Visibility.Collapsed;
        StateRing.Opacity = 0.16;
        _overlay.Hide();
    }

    private void ApplyState(
        OrbPresentationState state,
        string status,
        string detail,
        bool animateLiveState = false)
    {
        StatusText.Text = status.Trim();
        DetailText.Text = detail.Trim();
        StateText.Text = ToStateLabel(state);
        StateIcon.Glyph = GetGlyph(state);
        MicrophoneButton.IsEnabled = state is OrbPresentationState.Idle or OrbPresentationState.Done;
        CancelButton.IsEnabled = state is not OrbPresentationState.Idle and not OrbPresentationState.Done;
        bool idleControlsVisible = state is OrbPresentationState.Idle or OrbPresentationState.Done;
        IdleControlBar.Visibility = idleControlsVisible ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = idleControlsVisible ? Visibility.Collapsed : Visibility.Visible;
        SetStateVisual(state, animateLiveState);
        ApplyVisibleWindowRegion();
        AutomationProperties.SetName(OverlayRoot, $"Cursivis {status}. {detail}");
        UpdateMotion(state, animateLiveState);
    }

    private void ShowWithoutActivation()
    {
        OverlayRectangle bounds;
        if (_overlay.IsVisible)
        {
            OverlayRectangle current = _overlay.GetBounds();
            bounds = NativeWindowPositioner.GetRestoredPlacement(
                _overlay.WindowHandle,
                LogicalWidth,
                LogicalHeight,
                new OverlayPoint(current.X, current.Y));
        }
        else if (_userPlaced && _placementPoint is OverlayPoint restored)
        {
            bounds = NativeWindowPositioner.GetRestoredPlacement(
                _overlay.WindowHandle,
                LogicalWidth,
                LogicalHeight,
                restored);
        }
        else
        {
            bounds = NativeWindowPositioner.GetNearCursorPlacement(
                _overlay.WindowHandle,
                LogicalWidth,
                LogicalHeight);
        }

        AppWindow.Show(activateWindow: false);
        _overlay.Show(bounds, topmost: true, noActivate: true, CornerRadius);
        _ = DispatcherQueue.TryEnqueue(_overlay.EnforceBorderlessStyle);
        RequestBorderlessFramePass();
        StartEntranceAnimation();
    }

    private void RequestBorderlessFramePass()
    {
        if (_awaitingBorderlessFrame)
        {
            return;
        }

        _awaitingBorderlessFrame = true;
        _borderlessFrames = 0;
        CompositionTarget.Rendering += OnFirstOverlayFrame;
    }

    private void OnFirstOverlayFrame(object? sender, object args)
    {
        if (!_overlay.IsVisible)
        {
            FinishBorderlessFramePass();
            return;
        }

        _overlay.EnforceBorderlessStyle();
        _borderlessFrames = _overlay.HasBorderlessStyle ? _borderlessFrames + 1 : 0;
        if (_borderlessFrames >= 2)
        {
            FinishBorderlessFramePass();
        }
    }

    private void FinishBorderlessFramePass()
    {
        CompositionTarget.Rendering -= OnFirstOverlayFrame;
        _awaitingBorderlessFrame = false;
        _borderlessFrames = 0;
    }

    private void UpdateMotion(OrbPresentationState state, bool animateLiveState)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(StateRing);
        visual.StopAnimation(nameof(Visual.Scale));
        visual.Scale = Vector3.One;

        if (!animateLiveState ||
            state is not (
                OrbPresentationState.Listening or
                OrbPresentationState.Thinking or
                OrbPresentationState.Generating or
                OrbPresentationState.Speaking or
                OrbPresentationState.Guiding or
                OrbPresentationState.Executing) ||
            !new UISettings().AnimationsEnabled)
        {
            return;
        }

        Compositor compositor = visual.Compositor;
        Vector3KeyFrameAnimation pulse = compositor.CreateVector3KeyFrameAnimation();
        pulse.InsertKeyFrame(0, Vector3.One);
        pulse.InsertKeyFrame(0.5f, new Vector3(1.018f, 1.018f, 1));
        pulse.InsertKeyFrame(1, Vector3.One);
        pulse.Duration = TimeSpan.FromMilliseconds(state == OrbPresentationState.Listening ? 760 : 1100);
        pulse.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.CenterPoint = new Vector3(68, 68, 0);
        visual.StartAnimation(nameof(Visual.Scale), pulse);
    }

    private void StartEntranceAnimation()
    {
        if (!new UISettings().AnimationsEnabled)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(RootShell);
        Compositor compositor = visual.Compositor;
        visual.Opacity = 1;
        ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(1, 1);
        animation.Duration = TimeSpan.FromMilliseconds(90);
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private async void OnNativeMoveSizeExited(
        InputNonClientPointerSource sender,
        ExitedMoveSizeEventArgs args)
    {
        OverlayRectangle current = _overlay.GetBounds();
        OverlayRectangle clamped = NativeWindowPositioner.GetRestoredPlacement(
            _overlay.WindowHandle,
            LogicalWidth,
            LogicalHeight,
            new OverlayPoint(current.X, current.Y));
        _overlay.Show(clamped, topmost: true, noActivate: true, CornerRadius);
        _userPlaced = true;
        _placementPoint = new OverlayPoint(clamped.X, clamped.Y);
        try
        {
            await _placementStore.SaveAsync(_placementPoint.Value);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnMicrophoneClicked(object sender, RoutedEventArgs args) =>
        MicrophoneRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClicked(object sender, RoutedEventArgs args) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnCancelClicked(object sender, RoutedEventArgs args) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OnTranscriptToggleClicked(object sender, RoutedEventArgs args) =>
        LiveTranscriptVisibilityRequested?.Invoke(
            this,
            new LiveTranscriptVisibilityRequestedEventArgs(!_liveTranscriptPanelVisible));

    private void SetLiveTranscriptToggleVisible(bool visible, bool transcriptPanelVisible)
    {
        _liveTranscriptPanelVisible = transcriptPanelVisible;
        TranscriptToggleButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TranscriptToggleIcon.Glyph = transcriptPanelVisible ? "\uE890" : "\uE7B3";
        AutomationProperties.SetName(
            TranscriptToggleButton,
            transcriptPanelVisible ? "Hide Live transcript panel" : "Show Live transcript panel");
        ToolTipService.SetToolTip(
            TranscriptToggleButton,
            transcriptPanelVisible ? "Hide transcript" : "Show transcript");
        ApplyVisibleWindowRegion();
    }

    private void OnGuidedOperationClicked(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: GuidedOption option })
        {
            GuidedOperationRequested?.Invoke(
                this,
                new GuidedOperationRequestedEventArgs(option));
        }
    }

    private void OnCancelAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CompositionTarget.Rendering -= OnFirstOverlayFrame;
        _awaitingBorderlessFrame = false;
        AppWindow.Changed -= OnAppWindowChanged;
        _nonClientPointerSource.ExitedMoveSize -= OnNativeMoveSizeExited;
        _nonClientPointerSource.ClearAllRegionRects();
        Closed -= OnClosed;
        _transparentBackdrop.Dispose();
        _overlay.Dispose();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange && sender.IsVisible)
        {
            _ = DispatcherQueue.TryEnqueue(_overlay.EnforceBorderlessStyle);
            RequestBorderlessFramePass();
        }

        if (args.DidSizeChange)
        {
            ApplyVisibleWindowRegion();
        }
    }

    private void SetStateVisual(OrbPresentationState state, bool isLivePresentation)
    {
        Border[] visuals =
        [
            IdleStateVisual,
            ListeningStateVisual,
            ThinkingStateVisual,
            GeneratingStateVisual,
            SpeakingStateVisual,
            GuidingStateVisual,
            ExecutingStateVisual,
            DoneStateVisual,
            CancelledStateVisual,
            ErrorStateVisual,
        ];

        for (int index = 0; index < visuals.Length; index++)
        {
            visuals[index].Visibility = isLivePresentation && index == (int)state
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        StateRing.Visibility = isLivePresentation ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetGlyph(OrbPresentationState state) => state switch
    {
        OrbPresentationState.Idle => "\uE768",
        OrbPresentationState.Listening => "\uE720",
        OrbPresentationState.Thinking => "\uE895",
        OrbPresentationState.Generating => "\uE9D9",
        OrbPresentationState.Speaking => "\uE767",
        OrbPresentationState.Guiding => "\uE8F1",
        OrbPresentationState.Executing => "\uE90F",
        OrbPresentationState.Done => "\uE73E",
        OrbPresentationState.Cancelled => "\uE711",
        OrbPresentationState.Error => "\uE783",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string ToStateLabel(OrbPresentationState state) => state switch
    {
        OrbPresentationState.Thinking => "Processing",
        OrbPresentationState.Generating => "Generating",
        OrbPresentationState.Guiding => "Guided",
        OrbPresentationState.Executing => "Executing",
        _ => state.ToString(),
    };

    private void BuildGuidedOptions(IReadOnlyList<GuidedOption> dynamicOptions)
    {
        if (dynamicOptions.Count is < 3 or > GuidedOption.MaximumDynamicOptions ||
            dynamicOptions.Any(static option => option.IsCustomTask))
        {
            throw new ArgumentException("Guided Mode requires three or four model-generated operations.", nameof(dynamicOptions));
        }

        _guidedOptions = dynamicOptions
            .DistinctBy(static option => option.Id, StringComparer.OrdinalIgnoreCase)
            .Concat([GuidedOption.CustomTask])
            .ToArray();
        if (_guidedOptions.Length != dynamicOptions.Count + 1)
        {
            throw new ArgumentException("Guided Mode options must use distinct ids.", nameof(dynamicOptions));
        }

        ApplyGuidedOptions();
    }

    private void SetGuidedPanelVisible(bool visible)
    {
        foreach (Button button in GuidedActionButtons())
        {
            button.Visibility = visible && button.Tag is GuidedOption
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!visible)
        {
            _guidedOptions = [];
        }
    }

    private void ApplyVisibleWindowRegion()
    {
        var shapes = new List<OverlayRegionShape>
        {
            // The normal HWND is exactly the visible 118px core orb. This
            // avoids exposing the transparent 320px composition host.
            new(101, 101, 118, 118, isEllipse: true),
        };

        if (StateRing.Visibility == Visibility.Visible)
        {
            // Live Mode may include one close, faint halo around the core.
            shapes.Add(new OverlayRegionShape(92, 92, 136, 136, isEllipse: true));
        }

        if (IdleControlBar.Visibility == Visibility.Visible)
        {
            shapes.Add(new OverlayRegionShape(117, 250, 62, 30, cornerRadius: 15));
        }

        if (CancelButton.Visibility == Visibility.Visible)
        {
            shapes.Add(new OverlayRegionShape(145, 250, 30, 30, cornerRadius: 15));
        }

        if (TranscriptToggleButton.Visibility == Visibility.Visible)
        {
            shapes.Add(new OverlayRegionShape(182, 250, 30, 30, cornerRadius: 15));
        }

        AddGuidedChipRegion(ActionChipTop, 106, 18, shapes);
        AddGuidedChipRegion(ActionChipUpperRight, 206, 80, shapes);
        AddGuidedChipRegion(ActionChipLowerRight, 206, 200, shapes);
        AddGuidedChipRegion(ActionChipLowerLeft, 6, 200, shapes);
        AddGuidedChipRegion(ActionChipUpperLeft, 6, 80, shapes);
        _overlay.SetLogicalRegionShapes(shapes);
    }

    private static void AddGuidedChipRegion(
        Button button,
        int x,
        int y,
        ICollection<OverlayRegionShape> shapes)
    {
        if (button.Visibility == Visibility.Visible)
        {
            shapes.Add(new OverlayRegionShape(x, y, 108, 40, cornerRadius: 20));
        }
    }

    private void FocusFirstGuidedControl()
    {
        if (GuidedActionButtons().FirstOrDefault(
                static button => button.Visibility == Visibility.Visible) is Control first)
        {
            _ = first.Focus(FocusState.Programmatic);
        }
    }

    private Button[] GuidedActionButtons() =>
    [
        ActionChipTop,
        ActionChipUpperRight,
        ActionChipLowerRight,
        ActionChipLowerLeft,
        ActionChipUpperLeft,
    ];

    private void ApplyGuidedOptions()
    {
        Button[] buttons = GuidedActionButtons();
        for (int index = 0; index < buttons.Length; index++)
        {
            Button button = buttons[index];
            if (index >= _guidedOptions.Length)
            {
                button.Tag = null;
                button.Content = string.Empty;
                button.Visibility = Visibility.Collapsed;
                continue;
            }

            GuidedOption option = _guidedOptions[index];
            button.Tag = option;
            button.Content = CreateOptionLabel(option.Label);
            button.Visibility = Visibility.Visible;
            AutomationProperties.SetAutomationId(button, $"GuidedOption{option.Id}");
            AutomationProperties.SetName(button, option.Label);
            ToolTipService.SetToolTip(button, option.Label);
        }
    }

    private static TextBlock CreateOptionLabel(string label) => new()
    {
        Text = label,
        MaxLines = 2,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}

public sealed class GuidedOperationRequestedEventArgs(
    GuidedOption option) : EventArgs
{
    public GuidedOption Option { get; } = option ?? throw new ArgumentNullException(nameof(option));
}

public sealed class LiveTranscriptVisibilityRequestedEventArgs(bool visible) : EventArgs
{
    public bool Visible { get; } = visible;
}
