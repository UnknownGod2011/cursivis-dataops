using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Cursivis.Windows.App;

public sealed partial class QuickTaskInputWindow : Window
{
    private const int LogicalWidth = 380;
    private const int LogicalHeight = 104;
    private readonly TransparentWindowBackdrop _transparentBackdrop;
    private readonly NativeOverlayWindow _overlay;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _completed;
    private bool _wasActivated;
    private bool _awaitingBorderlessFrame;
    private int _borderlessFrames;

    public QuickTaskInputWindow(
        string taskName,
        ElementTheme theme = ElementTheme.Default,
        string? placeholder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        InitializeComponent();
        OverlayRoot.RequestedTheme = theme;
        Title = $"Cursivis {taskName.Trim()} input";
        PlaceholderText.Text = string.IsNullOrWhiteSpace(placeholder)
            ? $"{taskName.Trim()}…"
            : placeholder.Trim();
        _transparentBackdrop = new TransparentWindowBackdrop(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragHandle);
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClientPointerSource.ExitedMoveSize += OnNativeMoveSizeExited;
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _overlay = new NativeOverlayWindow(handle, noActivate: false);
        Activated += OnActivated;
        Closed += OnClosed;
    }

    public async Task<string?> ShowAsync(
        CancellationToken cancellationToken = default,
        OverlayRectangle? anchor = null)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            _ = DispatcherQueue.TryEnqueue(() => Complete(null));
        });
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        OverlayRectangle bounds = anchor is OverlayRectangle anchorBounds
            ? NativeWindowPositioner.GetAdjacentPlacement(
                handle,
                LogicalWidth,
                LogicalHeight,
                anchorBounds)
            : NativeWindowPositioner.GetNearCursorPlacement(
                handle,
                LogicalWidth,
                LogicalHeight);
        AppWindow.Show(activateWindow: false);
        _overlay.Show(bounds, topmost: true, noActivate: false, cornerRadius: 20);
        _ = _overlay.ActivateForInteraction();
        _ = DispatcherQueue.TryEnqueue(_overlay.EnforceBorderlessStyle);
        RequestBorderlessFramePass();
        _ = ContentTextBox.Focus(FocusState.Programmatic);
        return await _completion.Task;
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

    private void OnInputTextChanged(object sender, TextChangedEventArgs args)
    {
        bool hasInput = !string.IsNullOrWhiteSpace(ContentTextBox.Text);
        RunButton.IsEnabled = hasInput;
        PlaceholderText.Visibility = hasInput ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRunClicked(object sender, RoutedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(ContentTextBox.Text))
        {
            Complete(ContentTextBox.Text.Trim());
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs args) => Complete(null);

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Complete(null);
    }

    private void OnEnterInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (RunButton.IsEnabled)
        {
            Complete(ContentTextBox.Text.Trim());
        }
    }

    private void OnNativeMoveSizeExited(
        InputNonClientPointerSource sender,
        ExitedMoveSizeEventArgs args)
    {
        OverlayRectangle clamped = NativeWindowPositioner.ClampCurrentPlacement(
            _overlay.WindowHandle,
            _overlay.GetBounds());
        _overlay.Show(clamped, topmost: true, noActivate: false, cornerRadius: 20);
    }

    private void Complete(string? text)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _overlay.Hide();
        _completion.TrySetResult(text);
        Close();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_wasActivated)
            {
                Complete(null);
            }

            return;
        }

        _wasActivated = true;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        CompositionTarget.Rendering -= OnFirstOverlayFrame;
        _awaitingBorderlessFrame = false;
        Activated -= OnActivated;
        _nonClientPointerSource.ExitedMoveSize -= OnNativeMoveSizeExited;
        _nonClientPointerSource.ClearAllRegionRects();
        Closed -= OnClosed;
        if (!_completed)
        {
            _completed = true;
            _completion.TrySetResult(null);
        }

        _transparentBackdrop.Dispose();
        _overlay.Dispose();
    }
}
