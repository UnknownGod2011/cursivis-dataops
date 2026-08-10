using System.Numerics;
using Cursivis.Application.Context;
using Cursivis.Application.Presentation;
using Cursivis.Domain.Interaction;
using Cursivis.Infrastructure.Storage.Persistence;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace Cursivis.Windows.App;

public sealed partial class ContextResultWindow : Window
{
    private const int MinimumLogicalWidth = 516;
    private const int MinimumLogicalHeight = 232;
    private const int CornerRadius = 28;
    private readonly TransparentWindowBackdrop _transparentBackdrop;
    private readonly NativeOverlayWindow _overlay;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly OverlayPlacementStore _placementStore;
    private readonly OverlaySizeStore _sizeStore;
    private readonly DispatcherQueueTimer _noticeTimer;
    private OverlaySize? _savedLogicalSize;
    private OverlayPoint? _savedPlacement;
    private bool _userPlaced;
    private bool _awaitingBorderlessFrame;
    private int _borderlessFrames;
    private bool _nativeInteraction;
    private DetectedColor? _detectedColor;
    private ResultPanelPresentation _presentation = ResultPanelPresentation.Failure(
        "Waiting for context",
        "Select content and invoke Cursivis.");

    public ContextResultWindow()
    {
        InitializeComponent();
        Title = ResourceText.Get("ContextResultWindowTitle");
        AppWindow.SetIcon("Assets/AppIcon.ico");
        _transparentBackdrop = new TransparentWindowBackdrop(this);

        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsAlwaysOnTop = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        // InputNonClientPointerSource requires a resizable presenter to enter
        // the native size loop. NativeOverlayWindow removes the standard frame
        // bits and DWM border after every show, leaving only our custom shell.
        presenter.IsResizable = true;
        presenter.SetBorderAndTitleBar(false, false);
        ExtendsContentIntoTitleBar = true;
        // The complete header is the explicit move surface. Interactive
        // descendants are registered as passthrough regions below, so button,
        // text-selection, and resize input never begin a move operation.
        SetTitleBar(DragHeader);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _overlay = new NativeOverlayWindow(
            handle,
            noActivate: true,
            allowResize: true,
            MinimumLogicalWidth,
            MinimumLogicalHeight);
        _nonClientPointerSource.EnteredMoveSize += OnNativeMoveSizeEntered;
        _nonClientPointerSource.ExitedMoveSize += OnNativeMoveSizeExited;
        UpdateResizeRegions();
        _sizeStore = new OverlaySizeStore(
            CursivisStoragePaths.ForCurrentUser().ResultPanelSizeFile);
        _savedLogicalSize = _sizeStore.TryLoad();
        _placementStore = new OverlayPlacementStore(
            CursivisStoragePaths.ForCurrentUser().ResultPanelPlacementFile);
        _savedPlacement = _placementStore.TryLoad();
        _userPlaced = _savedPlacement is not null;
        _noticeTimer = DispatcherQueue.CreateTimer();
        _noticeTimer.IsRepeating = false;
        _noticeTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _noticeTimer.Tick += OnNoticeTimerTick;
        OverlayRoot.Loaded += OnOverlayRootLoaded;
        OverlayRoot.ActualThemeChanged += OnActualThemeChanged;
        OverlayRoot.SizeChanged += OnOverlayRootSizeChanged;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += OnClosed;
        ApplyPresentation(_presentation);
    }

    public event EventHandler? UndoRequested;

    public event EventHandler? InsertRequested;

    public event EventHandler? TakeActionRequested;

    public event EventHandler? MoreOptionsRequested;

    public event EventHandler? RefineRequested;

    public event EventHandler? SettingsRequested;

    public event Action<ElementTheme>? ThemeRequested;

    public event EventHandler? CloseRequested;

    public event Action<bool>? OverlayVisibilityChanged;

    public bool IsVisible => _overlay.IsVisible;

    public ElementTheme CurrentTheme => OverlayRoot.ActualTheme;

    public bool IsInteracting => _nativeInteraction;

    public OverlayRectangle OverlayBounds => _overlay.GetBounds();

    public void ShowResult(
        SmartResult result,
        bool guidedMode,
        string? operationLabel = null,
        bool? canReplace = null)
    {
        _detectedColor = null;
        _presentation = ResultPanelPresentation.FromResult(result, guidedMode);
        if (!string.IsNullOrWhiteSpace(operationLabel))
        {
            _presentation = _presentation with { OperationLabel = operationLabel.Trim() };
        }

        if (canReplace is not null)
        {
            _presentation = _presentation with { CanReplace = canReplace.Value };
        }

        if (guidedMode)
        {
            _presentation = _presentation.WithNotice(
                ResultPanelStatus.Notice,
                ResourceText.Get("ContextResultGuidedNoticeTitle"),
                ResourceText.Get("ContextResultGuidedNoticeMessage"));
        }

        ApplyPresentation(_presentation);
        ShowWithoutActivation();
    }

    public void ShowColorResult(SmartResult result, DetectedColor color)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(color);
        _detectedColor = color;
        _presentation = ResultPanelPresentation.FromResult(result, guidedMode: false) with
        {
            OperationLabel = ResourceText.Get("ContextResultColorDetectedOperation"),
            CanInsert = false,
            CanReplace = false,
            CanTakeAction = false,
            CanRefine = false,
        };
        ApplyPresentation(_presentation);
        ShowWithoutActivation();
    }

    public void ShowFailure(string heading, string message)
    {
        _detectedColor = null;
        _presentation = ResultPanelPresentation.Failure(heading, message);
        ApplyPresentation(_presentation);
        ShowWithoutActivation();
    }

    public void ShowActionOutcome(string message, bool canUndo)
    {
        _detectedColor = null;
        _presentation = ResultPanelPresentation.ActionOutcome(message, canUndo);
        ApplyPresentation(_presentation);
        ShowWithoutActivation();
    }

    public void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        _noticeTimer.Stop();
        _presentation = _presentation.WithNotice(
            severity == InfoBarSeverity.Error ? ResultPanelStatus.Error : ResultPanelStatus.Notice,
            title,
            message);
        ApplyPresentation(_presentation);
        if (!_overlay.IsVisible)
        {
            ShowWithoutActivation();
        }

        if (severity == InfoBarSeverity.Success)
        {
            _noticeTimer.Start();
        }
    }

    public void SetRequestedTheme(ElementTheme theme)
    {
        OverlayRoot.RequestedTheme = theme;
        UpdateThemeIcon();
    }

    public void SetUndoAvailable(bool isAvailable)
    {
        _presentation = _presentation with { CanUndo = isAvailable };
        UndoButton.IsEnabled = isAvailable;
    }

    private void OnNoticeTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_presentation.Status != ResultPanelStatus.Notice)
        {
            return;
        }

        _presentation = _presentation with
        {
            Status = ResultPanelStatus.Ready,
            NoticeTitle = null,
            NoticeMessage = null,
        };
        ApplyPresentation(_presentation);
    }

    public void Hide()
    {
        bool wasVisible = _overlay.IsVisible;
        _overlay.Hide();
        _overlay.SetNoActivate(true);
        if (wasVisible)
        {
            OverlayVisibilityChanged?.Invoke(false);
        }
    }

    private void ApplyPresentation(ResultPanelPresentation presentation)
    {
        OperationLabelText.Text = presentation.OperationLabel;
        ContextLabelText.Text = presentation.ContextLabel;
        ResultText.Text = string.IsNullOrWhiteSpace(presentation.Content)
            ? presentation.NoticeMessage ?? string.Empty
            : presentation.Content;
        bool showsColor = _detectedColor is not null;
        ColorPreviewColumn.Width = new GridLength(showsColor ? 104 : 0);
        ColorPreviewBorder.Visibility = showsColor ? Visibility.Visible : Visibility.Collapsed;
        if (_detectedColor is { } color)
        {
            ColorPreviewBorder.Background = new SolidColorBrush(
                global::Windows.UI.Color.FromArgb(255, color.Red, color.Green, color.Blue));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                ColorPreviewBorder,
                $"{color.ApproximateName}, {color.Hex}");
        }
        StreamingRing.IsActive = presentation.Status == ResultPanelStatus.Streaming;
        StreamingRing.Visibility = StreamingRing.IsActive ? Visibility.Visible : Visibility.Collapsed;

        UndoButton.IsEnabled = presentation.CanUndo;
        InsertButton.IsEnabled = presentation.CanInsert;
        TakeActionButton.IsEnabled = presentation.CanTakeAction;
        RefineButton.IsEnabled = presentation.CanRefine;

        bool hasNotice = !string.IsNullOrWhiteSpace(presentation.Content) &&
                         !string.IsNullOrWhiteSpace(presentation.NoticeTitle) &&
                         !string.IsNullOrWhiteSpace(presentation.NoticeMessage);
        StatusMessageText.Visibility = hasNotice ? Visibility.Visible : Visibility.Collapsed;
        StatusMessageText.Text = presentation.Status == ResultPanelStatus.Notice
            ? presentation.NoticeTitle ?? string.Empty
            : string.Join(": ", presentation.NoticeTitle, presentation.NoticeMessage);
        ToolTipService.SetToolTip(StatusMessageText, presentation.NoticeMessage);
        bool isError = presentation.Status == ResultPanelStatus.Error;
        StatusMessageText.Foreground = GetThemeBrush(
            isError ? "OverlayErrorTextBrush" : "OverlayMutedTextBrush");
    }

    private void ShowWithoutActivation()
    {
        bool wasVisible = _overlay.IsVisible;
        OverlayRectangle bounds;
        if (_overlay.IsVisible)
        {
            bounds = NativeWindowPositioner.ClampCurrentPlacement(
                _overlay.WindowHandle,
                _overlay.GetBounds());
        }
        else
        {
            ResultPanelLogicalSize calculated = ResultPanelSizeCalculator.Calculate(
                _presentation.Content,
                !string.IsNullOrWhiteSpace(_presentation.NoticeMessage));
            OverlaySize logicalSize = _savedLogicalSize ?? new OverlaySize(
                calculated.Width,
                calculated.Height);
            logicalSize = new OverlaySize(
                Math.Max(MinimumLogicalWidth, logicalSize.Width),
                Math.Max(MinimumLogicalHeight, logicalSize.Height));
            bounds = _userPlaced && _savedPlacement is OverlayPoint placement
                ? NativeWindowPositioner.GetRestoredPlacement(
                    _overlay.WindowHandle,
                    logicalSize.Width,
                    logicalSize.Height,
                    placement)
                : NativeWindowPositioner.GetNearCursorPlacement(
                    _overlay.WindowHandle,
                    logicalSize.Width,
                    logicalSize.Height);
        }

        AppWindow.Show(activateWindow: false);
        _overlay.Show(bounds, topmost: true, noActivate: true, CornerRadius);
        _ = DispatcherQueue.TryEnqueue(EnforceCustomChrome);
        RequestBorderlessFramePass();
        if (!wasVisible)
        {
            OverlayVisibilityChanged?.Invoke(true);
        }

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

        EnforceCustomChrome();
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

    private void EnforceCustomChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
        }

        _overlay.EnforceBorderlessStyle();
    }

    private void StartEntranceAnimation()
    {
        var settings = new UISettings();
        if (!settings.AnimationsEnabled)
        {
            RootShell.Opacity = 1;
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(RootShell);
        Compositor compositor = visual.Compositor;
        visual.Opacity = 1;
        visual.Offset = Vector3.Zero;

        ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(110);
        Vector3KeyFrameAnimation rise = compositor.CreateVector3KeyFrameAnimation();
        rise.InsertKeyFrame(0, new Vector3(0, 8, 0));
        rise.InsertKeyFrame(1, Vector3.Zero, compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.2f, 0.8f),
            new Vector2(0.2f, 1f)));
        rise.Duration = TimeSpan.FromMilliseconds(125);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation(nameof(Visual.Offset), rise);
    }

    private void OnOverlayPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (args.GetCurrentPoint(OverlayRoot).Properties.IsLeftButtonPressed && _overlay.IsNoActivate)
        {
            _ = _overlay.ActivateForInteraction();
        }
    }

    private void OnUndoClicked(object sender, RoutedEventArgs args) =>
        UndoRequested?.Invoke(this, EventArgs.Empty);

    private void OnInsertClicked(object sender, RoutedEventArgs args) =>
        InsertRequested?.Invoke(this, EventArgs.Empty);

    private void OnTakeActionClicked(object sender, RoutedEventArgs args) =>
        TakeActionRequested?.Invoke(this, EventArgs.Empty);

    private void OnMoreOptionsClicked(object sender, RoutedEventArgs args) =>
        MoreOptionsRequested?.Invoke(this, EventArgs.Empty);

    private void OnRefineClicked(object sender, RoutedEventArgs args) =>
        RefineRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClicked(object sender, RoutedEventArgs args) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);


    private void OnThemeClicked(object sender, RoutedEventArgs args)
    {
        ElementTheme requested = OverlayRoot.ActualTheme == ElementTheme.Dark
            ? ElementTheme.Light
            : ElementTheme.Dark;
        SetRequestedTheme(requested);
        ThemeRequested?.Invoke(requested);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs args) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseAcceleratorInvoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateThemeIcon();
        ApplyPresentation(_presentation);
    }

    private void OnOverlayRootLoaded(object sender, RoutedEventArgs args) =>
        UpdateThemeIcon();

    private Brush GetThemeBrush(string resourceKey)
    {
        string themeKey = new AccessibilitySettings().HighContrast
            ? "HighContrast"
            : OverlayRoot.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        IList<ResourceDictionary> dictionaries =
            Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries;
        for (int index = dictionaries.Count - 1; index >= 0; index--)
        {
            ResourceDictionary dictionary = dictionaries[index];
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out object? themeValue) &&
                themeValue is ResourceDictionary themeDictionary &&
                themeDictionary.TryGetValue(resourceKey, out object? resource) &&
                resource is Brush brush)
            {
                return brush;
            }
        }

        throw new InvalidOperationException($"Overlay theme resource '{resourceKey}' is unavailable.");
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange && sender.IsVisible)
        {
            _ = DispatcherQueue.TryEnqueue(EnforceCustomChrome);
            RequestBorderlessFramePass();
        }

        if (args.DidSizeChange)
        {
            UpdateResizeRegions();
            _overlay.UpdateRoundedRegion(CornerRadius);
        }
    }

    private void UpdateResizeRegions()
    {
        int width = Math.Max(1, AppWindow.Size.Width);
        int height = Math.Max(1, AppWindow.Size.Height);
        double scale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        int border = Math.Clamp((int)Math.Ceiling(8d * scale), 6, 32);

        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.TopBorder,
            [new RectInt32(0, 0, width, Math.Min(border, height))]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.LeftBorder,
            [new RectInt32(0, 0, Math.Min(border, width), height)]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.BottomBorder,
            [new RectInt32(0, Math.Max(0, height - border), width, Math.Min(border, height))]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.RightBorder,
            [new RectInt32(Math.Max(0, width - border), 0, Math.Min(border, width), height)]);
        _nonClientPointerSource.SetRegionRects(
            NonClientRegionKind.Passthrough,
            GetInteractiveHeaderRegions());
    }

    private RectInt32[] GetInteractiveHeaderRegions()
    {
        FrameworkElement[] controls =
        [
            UndoButton,
            InsertButton,
            TakeActionButton,
            MoreOptionsButton,
            ThemeButton,
            SettingsButton,
        ];

        return controls
            .Where(static control => control.ActualWidth > 0 && control.ActualHeight > 0)
            .Select(GetBoundsInOverlay)
            .ToArray();
    }

    private RectInt32 GetBoundsInOverlay(FrameworkElement element)
    {
        var origin = element.TransformToVisual(OverlayRoot).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        double scale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1d;
        return new RectInt32(
            Math.Max(0, (int)Math.Floor(origin.X * scale)),
            Math.Max(0, (int)Math.Floor(origin.Y * scale)),
            Math.Max(1, (int)Math.Ceiling(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight * scale)));
    }

    private void OnNativeMoveSizeEntered(
        InputNonClientPointerSource sender,
        EnteredMoveSizeEventArgs args) =>
        _nativeInteraction = true;

    private void OnOverlayRootSizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdateResizeRegions();

    private void OnNativeMoveSizeExited(
        InputNonClientPointerSource sender,
        ExitedMoveSizeEventArgs args)
    {
        _nativeInteraction = false;
        EnforceCustomChrome();
        RequestBorderlessFramePass();
        OnOverlayMoveOrResizeCompleted(sender, EventArgs.Empty);
    }

    private async void OnOverlayMoveOrResizeCompleted(object? sender, EventArgs args)
    {
        try
        {
            OverlayRectangle current = _overlay.GetBounds();
            OverlayRectangle clamped = NativeWindowPositioner.ClampCurrentPlacement(
                _overlay.WindowHandle,
                current);
            // Completing a WinUI title-bar move can restore a presenter frame
            // without changing the AppWindow size. Re-showing the same bounded
            // rectangle reapplies the transparent region and utility chrome as
            // one atomic native operation, while retaining no-activation.
            _overlay.Show(
                clamped,
                topmost: true,
                noActivate: _overlay.IsNoActivate,
                CornerRadius);

            _savedLogicalSize = NativeWindowPositioner.GetLogicalSize(
                _overlay.WindowHandle,
                clamped);
            _savedPlacement = new OverlayPoint(clamped.X, clamped.Y);
            _userPlaced = true;
            await Task.WhenAll(
                _sizeStore.SaveAsync(_savedLogicalSize.Value),
                _placementStore.SaveAsync(_savedPlacement.Value));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void UpdateThemeIcon()
    {
        // Match the original Cursivis language: show the active theme, with a
        // deliberately unmistakable symbol at compact overlay sizes.
        ThemeIcon.Glyph = OverlayRoot.ActualTheme == ElementTheme.Dark ? "\u263E" : "\u2600";
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CompositionTarget.Rendering -= OnFirstOverlayFrame;
        _awaitingBorderlessFrame = false;
        if (_overlay.IsVisible)
        {
            OverlayVisibilityChanged?.Invoke(false);
        }

        OverlayRoot.ActualThemeChanged -= OnActualThemeChanged;
        OverlayRoot.Loaded -= OnOverlayRootLoaded;
        OverlayRoot.SizeChanged -= OnOverlayRootSizeChanged;
        _noticeTimer.Stop();
        _noticeTimer.Tick -= OnNoticeTimerTick;
        AppWindow.Changed -= OnAppWindowChanged;
        _nonClientPointerSource.EnteredMoveSize -= OnNativeMoveSizeEntered;
        _nonClientPointerSource.ExitedMoveSize -= OnNativeMoveSizeExited;
        _nonClientPointerSource.ClearAllRegionRects();
        Closed -= OnClosed;
        _transparentBackdrop.Dispose();
        _overlay.Dispose();
    }
}
