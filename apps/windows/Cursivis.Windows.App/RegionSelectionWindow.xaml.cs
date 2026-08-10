using System.Runtime.InteropServices;
using Cursivis.Application.Context;
using Cursivis.Windows.App.Helpers;
using Cursivis.Windows.Platform.Capture;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Cursivis.Windows.App;

internal enum RegionSelectionKind
{
    Region,
    TooSmall,
    Cancelled,
}

internal sealed record RegionSelection(
    RegionSelectionKind Kind,
    OverlayRectangle? Region,
    ScreenAnchor Anchor);

public sealed partial class RegionSelectionWindow : Window
{
    private const int ClickThresholdPixels = 8;
    private readonly WindowsScreenCaptureService _screenCapture;
    private readonly NativeOverlayWindow _overlay;
    private OverlayRectangle _monitorBounds;
    private Point? _start;
    private uint? _capturedPointerId;
    private bool _completed;

    public RegionSelectionWindow(WindowsScreenCaptureService screenCapture)
    {
        InitializeComponent();
        Title = "Cursivis visual selection";
        _screenCapture = screenCapture ?? throw new ArgumentNullException(nameof(screenCapture));
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _overlay = new NativeOverlayWindow(handle, noActivate: false);
        Closed += OnClosed;
    }

    internal event EventHandler<RegionSelection>? SelectionCompleted;

    public async Task BeginSelectionAsync()
    {
        (_monitorBounds, _) = NativeWindowPositioner.GetCursorMonitor();
        CapturedScreenImage preview = _screenCapture.Capture(_monitorBounds);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(preview.EncodedBytes);
            await writer.StoreAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        MonitorPreview.Source = bitmap;
        if (_completed)
        {
            return;
        }

        _overlay.Show(
            _monitorBounds,
            topmost: true,
            noActivate: false,
            cornerRadius: 0);
        _ = _overlay.ActivateForInteraction();
        ApplyCrossCursor();
        _ = SelectionRoot.Focus(FocusState.Programmatic);
    }

    public void CancelSelection()
    {
        if (_completed)
        {
            return;
        }

        Complete(new RegionSelection(
            RegionSelectionKind.Cancelled,
            null,
            new ScreenAnchor(0, 0)));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        ApplyCrossCursor();
        PointerPoint point = args.GetCurrentPoint(SelectionRoot);
        if (!point.Properties.IsLeftButtonPressed || _start is not null)
        {
            return;
        }

        _start = point.Position;
        _capturedPointerId = args.Pointer.PointerId;
        _ = SelectionRoot.CapturePointer(args.Pointer);
        SelectionRectangle.Visibility = Visibility.Visible;
        UpdateRectangle(point.Position);
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        ApplyCrossCursor();
        if (_start is null || _capturedPointerId != args.Pointer.PointerId)
        {
            return;
        }

        UpdateRectangle(args.GetCurrentPoint(SelectionRoot).Position);
        args.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_start is not Point start || _capturedPointerId != args.Pointer.PointerId)
        {
            return;
        }

        Point end = args.GetCurrentPoint(SelectionRoot).Position;
        SelectionRoot.ReleasePointerCapture(args.Pointer);
        _capturedPointerId = null;
        _start = null;
        double scale = SelectionRoot.XamlRoot?.RasterizationScale ?? 1d;
        int left = _monitorBounds.X + (int)Math.Floor(Math.Min(start.X, end.X) * scale);
        int top = _monitorBounds.Y + (int)Math.Floor(Math.Min(start.Y, end.Y) * scale);
        int width = Math.Max(1, (int)Math.Ceiling(Math.Abs(end.X - start.X) * scale));
        int height = Math.Max(1, (int)Math.Ceiling(Math.Abs(end.Y - start.Y) * scale));
        int anchorX = _monitorBounds.X + (int)Math.Round(end.X * scale);
        int anchorY = _monitorBounds.Y + (int)Math.Round(end.Y * scale);
        ScreenAnchor anchor = new(anchorX, anchorY);

        RegionSelection selection = width < ClickThresholdPixels || height < ClickThresholdPixels
            ? new RegionSelection(RegionSelectionKind.TooSmall, null, anchor)
            : new RegionSelection(
                RegionSelectionKind.Region,
                new OverlayRectangle(left, top, width, height),
                anchor);
        Complete(selection);
        args.Handled = true;
    }

    private void UpdateRectangle(Point current)
    {
        if (_start is not Point start)
        {
            return;
        }

        double left = Math.Min(start.X, current.X);
        double top = Math.Min(start.Y, current.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = Math.Abs(current.X - start.X);
        SelectionRectangle.Height = Math.Abs(current.Y - start.Y);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CancelSelection();
    }

    private void Complete(RegionSelection selection)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _overlay.Hide();
        SelectionCompleted?.Invoke(this, selection);
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        if (!_completed)
        {
            _completed = true;
            SelectionCompleted?.Invoke(
                this,
                new RegionSelection(
                    RegionSelectionKind.Cancelled,
                    null,
                    new ScreenAnchor(0, 0)));
        }

        _overlay.Dispose();
    }

    private static void ApplyCrossCursor()
    {
        nint cursor = LoadCursor(nint.Zero, new nint(32515));
        if (cursor != nint.Zero)
        {
            _ = SetCursor(cursor);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint LoadCursor(nint instance, nint cursorName);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);
}
