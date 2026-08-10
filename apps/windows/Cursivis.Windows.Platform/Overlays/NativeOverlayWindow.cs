using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Cursivis.Windows.Platform.Overlays;

/// <summary>
/// Applies utility-overlay HWND behavior without coupling presentation state to WinUI.
/// </summary>
public sealed class NativeOverlayWindow : IDisposable
{
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmNcPaint = 0x0085;
    private const uint WmNcActivate = 0x0086;
    private const uint WmGetMinMaxInfo = 0x0024;
    private const uint WmNcDestroy = 0x0082;
    private const nuint HtCaption = 2;
    private const nuint SubclassId = 0x43564F4C; // "CVOL"
    private const int ExtendedStyleIndex = -20;
    private const int WindowStyleIndex = -16;
    private const long ExtendedStyleToolWindow = 0x00000080L;
    private const long ExtendedStyleWindowEdge = 0x00000100L;
    private const long ExtendedStyleClientEdge = 0x00000200L;
    private const long ExtendedStyleStaticEdge = 0x00020000L;
    private const long ExtendedStyleAppWindow = 0x00040000L;
    private const long ExtendedStyleNoActivate = 0x08000000L;
    private const long WindowStyleCaption = 0x00C00000L;
    private const long WindowStyleSystemMenu = 0x00080000L;
    private const long WindowStyleThickFrame = 0x00040000L;
    private const long WindowStyleMinimizeBox = 0x00020000L;
    private const long WindowStyleMaximizeBox = 0x00010000L;
    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoSize = 0x0001;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionFrameChanged = 0x0020;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint SetWindowPositionHideWindow = 0x0080;
    private const int ShowNoActivate = 4;
    private const int HideWindow = 0;
    private const int DwmWindowAttributeBorderColor = 34;
    private const int DwmWindowAttributeNcRenderingPolicy = 2;
    private const uint DwmNcRenderingDisabled = 1;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private static readonly nint Topmost = new(-1);
    private static readonly nint NotTopmost = new(-2);
    private readonly nint _windowHandle;
    private readonly bool _allowResize;
    private readonly int _minimumLogicalWidth;
    private readonly int _minimumLogicalHeight;
    private readonly SubclassProcedure _subclassProcedure;
    private OverlayRegionShape[]? _logicalRegionShapes;
    private bool _subclassAttached;
    private bool _disposed;

    public NativeOverlayWindow(
        nint windowHandle,
        bool noActivate,
        bool allowResize = false,
        int minimumLogicalWidth = 1,
        int minimumLogicalHeight = 1)
    {
        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("An overlay HWND is required.", nameof(windowHandle));
        }

        if (minimumLogicalWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLogicalWidth));
        }

        if (minimumLogicalHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLogicalHeight));
        }

        _windowHandle = windowHandle;
        _allowResize = allowResize;
        _minimumLogicalWidth = minimumLogicalWidth;
        _minimumLogicalHeight = minimumLogicalHeight;
        _subclassProcedure = WindowProcedure;
        _subclassAttached = SetWindowSubclass(
            _windowHandle,
            _subclassProcedure,
            SubclassId,
            0);
        if (!_subclassAttached)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The overlay native window hook could not be installed.");
        }

        ConfigureUtilityStyles(noActivate);
        ConfigureBorderlessStyle();
        SuppressDwmBorder();
    }

    public nint WindowHandle => _windowHandle;

    public bool IsNoActivate { get; private set; }

    public bool IsVisible => !_disposed && IsWindowVisible(_windowHandle);

    public bool HasBorderlessStyle
    {
        get
        {
            if (_disposed)
            {
                return false;
            }

            long prohibitedStyle = WindowStyleCaption |
                                   WindowStyleSystemMenu |
                                   WindowStyleMinimizeBox |
                                   WindowStyleMaximizeBox;
            if (!_allowResize)
            {
                prohibitedStyle |= WindowStyleThickFrame;
            }

            long prohibitedExtendedStyle = ExtendedStyleAppWindow |
                                           ExtendedStyleWindowEdge |
                                           ExtendedStyleClientEdge |
                                           ExtendedStyleStaticEdge;
            return (GetWindowLongPtr(_windowHandle, WindowStyleIndex).ToInt64() & prohibitedStyle) == 0 &&
                   (GetWindowLongPtr(_windowHandle, ExtendedStyleIndex).ToInt64() & prohibitedExtendedStyle) == 0;
        }
    }

    public void EnforceBorderlessStyle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ConfigureUtilityStyles(IsNoActivate);
        ConfigureBorderlessStyle();
        SuppressDwmBorder();
    }

    public void SetNoActivate(bool noActivate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ConfigureUtilityStyles(noActivate);
    }

    public void Show(OverlayRectangle bounds, bool topmost, bool noActivate, int cornerRadius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetNoActivate(noActivate);
        // AppWindow/OverlappedPresenter can restore native frame bits when the
        // window is shown. Enforce borderless utility chrome after that step.
        ConfigureBorderlessStyle();
        SuppressDwmBorder();
        ApplyConfiguredRegion(bounds.Width, bounds.Height, cornerRadius);

        // Showing an overlay should never take focus merely because it appeared.
        // WS_EX_NOACTIVATE remains a separate choice for status-only overlays.
        uint flags = SetWindowPositionShowWindow | SetWindowPositionNoActivate;

        if (!SetWindowPos(
                _windowHandle,
                topmost ? Topmost : NotTopmost,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                flags))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay could not be positioned.");
        }

        _ = ShowWindow(_windowHandle, ShowNoActivate);
        // ShowWindow can cause the presenter to restore overlapped-window frame
        // bits once more, so make the visible HWND definitively borderless.
        ConfigureBorderlessStyle();
        SuppressDwmBorder();
    }

    public void UpdateRoundedRegion(int cornerRadius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GetWindowRect(_windowHandle, out NativeRectangle rectangle))
        {
            _logicalRegionShapes = null;
            ApplyRoundedRegion(
                Math.Max(1, rectangle.Right - rectangle.Left),
                Math.Max(1, rectangle.Bottom - rectangle.Top),
                cornerRadius);
        }
    }

    /// <summary>
    /// Restricts the HWND to its intended visible surfaces. This prevents
    /// transparent WinUI pixels from revealing an opaque swap-chain host.
    /// </summary>
    public void SetLogicalRegionShapes(IReadOnlyList<OverlayRegionShape> shapes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(shapes);
        if (shapes.Count == 0)
        {
            throw new ArgumentException(
                "At least one overlay region shape is required.",
                nameof(shapes));
        }

        _logicalRegionShapes = shapes.ToArray();
        ApplyConfiguredRegionForCurrentBounds();
    }

    public bool ActivateForInteraction()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetNoActivate(false);
        return SetForegroundWindow(_windowHandle);
    }

    public OverlayRectangle GetBounds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!GetWindowRect(_windowHandle, out NativeRectangle rectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay bounds could not be read.");
        }

        return new(
            rectangle.Left,
            rectangle.Top,
            Math.Max(1, rectangle.Right - rectangle.Left),
            Math.Max(1, rectangle.Bottom - rectangle.Top));
    }

    public void Hide()
    {
        if (_disposed)
        {
            return;
        }

        _ = SetWindowPos(
            _windowHandle,
            NotTopmost,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate |
            SetWindowPositionHideWindow);
        _ = ShowWindow(_windowHandle, HideWindow);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        if (_subclassAttached)
        {
            _ = RemoveWindowSubclass(
                _windowHandle,
                _subclassProcedure,
                SubclassId);
            _subclassAttached = false;
        }

        _disposed = true;
    }

    private void ConfigureUtilityStyles(bool noActivate)
    {
        nint stylePointer = GetWindowLongPtr(_windowHandle, ExtendedStyleIndex);
        long style = stylePointer.ToInt64();
        style |= ExtendedStyleToolWindow;
        style &= ~(ExtendedStyleAppWindow |
                   ExtendedStyleWindowEdge |
                   ExtendedStyleClientEdge |
                   ExtendedStyleStaticEdge);
        if (noActivate)
        {
            style |= ExtendedStyleNoActivate;
        }
        else
        {
            style &= ~ExtendedStyleNoActivate;
        }

        _ = SetWindowLongPtr(_windowHandle, ExtendedStyleIndex, new nint(style));
        int error = Marshal.GetLastWin32Error();
        if (!SetWindowPos(
                _windowHandle,
                nint.Zero,
                0,
                0,
                0,
                0,
                SetWindowPositionNoMove |
                SetWindowPositionNoSize |
                SetWindowPositionNoActivate |
                SetWindowPositionFrameChanged) && error != 0)
        {
            throw new Win32Exception(error, "The overlay utility styles could not be applied.");
        }

        IsNoActivate = noActivate;
    }

    private void ConfigureBorderlessStyle()
    {
        nint stylePointer = GetWindowLongPtr(_windowHandle, WindowStyleIndex);
        long style = stylePointer.ToInt64();
        long removableStyle = WindowStyleCaption |
                              WindowStyleSystemMenu |
                              WindowStyleMinimizeBox |
                              WindowStyleMaximizeBox;
        if (!_allowResize)
        {
            removableStyle |= WindowStyleThickFrame;
        }

        style &= ~removableStyle;
        _ = SetWindowLongPtr(_windowHandle, WindowStyleIndex, new nint(style));
        RefreshFrame();
    }

    private void RefreshFrame()
    {
        _ = SetWindowPos(
            _windowHandle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SetWindowPositionNoMove |
            SetWindowPositionNoSize |
            SetWindowPositionNoActivate |
            SetWindowPositionFrameChanged);
    }

    private void SuppressDwmBorder()
    {
        uint nonClientRendering = DwmNcRenderingDisabled;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowAttributeNcRenderingPolicy,
            ref nonClientRendering,
            sizeof(uint));
        uint borderColor = DwmColorNone;
        _ = DwmSetWindowAttribute(
            _windowHandle,
            DwmWindowAttributeBorderColor,
            ref borderColor,
            sizeof(uint));
    }

    private void ApplyConfiguredRegionForCurrentBounds()
    {
        if (GetWindowRect(_windowHandle, out NativeRectangle rectangle))
        {
            ApplyConfiguredRegion(
                Math.Max(1, rectangle.Right - rectangle.Left),
                Math.Max(1, rectangle.Bottom - rectangle.Top),
                cornerRadius: 1);
        }
    }

    private void ApplyConfiguredRegion(int width, int height, int cornerRadius)
    {
        if (_logicalRegionShapes is not { Length: > 0 } shapes)
        {
            ApplyRoundedRegion(width, height, cornerRadius);
            return;
        }

        double scale = Math.Max(96, GetDpiForWindow(_windowHandle)) / 96d;
        nint composite = nint.Zero;
        try
        {
            foreach (OverlayRegionShape shape in shapes)
            {
                int left = (int)Math.Round(shape.X * scale);
                int top = (int)Math.Round(shape.Y * scale);
                int right = left + Math.Max(1, (int)Math.Round(shape.Width * scale));
                int bottom = top + Math.Max(1, (int)Math.Round(shape.Height * scale));
                int radius = Math.Max(
                    1,
                    (int)Math.Round(shape.CornerRadius * scale) * 2);
                nint part = shape.IsEllipse
                    ? CreateEllipticRgn(left, top, right + 1, bottom + 1)
                    : CreateRoundRectRgn(
                        left,
                        top,
                        right + 1,
                        bottom + 1,
                        radius,
                        radius);
                if (part == nint.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The overlay region could not be created.");
                }

                if (composite == nint.Zero)
                {
                    composite = part;
                    continue;
                }

                int combineResult = CombineRgn(composite, composite, part, RegionOr);
                _ = DeleteObject(part);
                if (combineResult == RegionError)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "The overlay region could not be combined.");
                }
            }

            if (SetWindowRgn(_windowHandle, composite, true) == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The overlay shape could not be applied.");
            }

            // SetWindowRgn owns the region after a successful call.
            composite = nint.Zero;
        }
        finally
        {
            if (composite != nint.Zero)
            {
                _ = DeleteObject(composite);
            }
        }
    }

    private void ApplyRoundedRegion(int width, int height, int cornerRadius)
    {
        double scale = Math.Max(96, GetDpiForWindow(_windowHandle)) / 96d;
        int radius = Math.Clamp(
            (int)Math.Round(cornerRadius * scale),
            1,
            Math.Min(width, height) / 2);
        nint region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay shape could not be created.");
        }

        if (SetWindowRgn(_windowHandle, region, true) == 0)
        {
            _ = DeleteObject(region);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The overlay shape could not be applied.");
        }
    }

    private nint WindowProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData)
    {
        if (message == WmNcLButtonDown && wParam == HtCaption && IsNoActivate)
        {
            // A deliberate caption drag on any utility overlay must not need a
            // sacrificial activation click. Registered interactive controls
            // are Passthrough regions and do not enter this path.
            ConfigureUtilityStyles(noActivate: false);
            _ = SetForegroundWindow(_windowHandle);
        }

        if (_allowResize)
        {
            switch (message)
            {
                case WmNcCalcSize:
                    // Retain WS_THICKFRAME for the native size loop while
                    // making the complete window client-rendered.
                    return nint.Zero;
                case WmNcPaint:
                    // The shell paints every visible pixel. Suppress the
                    // classic tracking frame that USER can restore after a
                    // custom title-bar move.
                    return nint.Zero;
                case WmNcActivate:
                    // Acknowledge activation without repainting non-client
                    // chrome; interactive controls still activate normally.
                    return new nint(1);
                case WmGetMinMaxInfo:
                    ApplyResizeConstraints(lParam);
                    break;
            }
        }

        if (message == WmNcDestroy)
        {
            _ = RemoveWindowSubclass(windowHandle, _subclassProcedure, subclassId);
            _subclassAttached = false;
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void ApplyResizeConstraints(nint lParam)
    {
        if (lParam == nint.Zero)
        {
            return;
        }

        MinMaxInfo info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        int dpi = Math.Max(96, unchecked((int)GetDpiForWindow(_windowHandle)));
        info.MinimumTrackSize.X = Math.Max(
            1,
            (int)Math.Round(_minimumLogicalWidth * dpi / 96d));
        info.MinimumTrackSize.Y = Math.Max(
            1,
            (int)Math.Round(_minimumLogicalHeight * dpi / 96d));

        nint monitor = MonitorFromWindow(_windowHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            int workWidth = monitorInfo.Work.Right - monitorInfo.Work.Left;
            int workHeight = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
            info.MaximumPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
            info.MaximumPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
            info.MaximumSize.X = workWidth;
            info.MaximumSize.Y = workHeight;
            info.MaximumTrackSize.X = workWidth;
            info.MaximumTrackSize.Y = workHeight;
        }

        Marshal.StructureToPtr(info, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
    }

    private const uint MonitorDefaultToNearest = 0x00000002;

    private delegate nint SubclassProcedure(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam,
        nuint subclassId,
        nuint referenceData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref uint attributeValue,
        int attributeSize);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateEllipticRgn(int left, int top, int right, int bottom);

    private const int RegionError = 0;
    private const int RegionOr = 2;

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint windowHandle, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId,
        nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint windowHandle,
        SubclassProcedure procedure,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(
        nint windowHandle,
        uint message,
        nuint wParam,
        nint lParam);
}
