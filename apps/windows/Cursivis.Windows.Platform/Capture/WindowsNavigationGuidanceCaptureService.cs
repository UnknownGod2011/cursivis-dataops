using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Cursivis.Application.Context;
using Cursivis.Application.Realtime;
using Cursivis.Domain.Context;
using Cursivis.Domain.Settings;
using Cursivis.Windows.Platform.Foreground;
using Cursivis.Windows.Platform.Overlays;

namespace Cursivis.Windows.Platform.Capture;

public sealed class WindowsNavigationGuidanceCaptureService(
    IForegroundWindowIdentityProvider foreground,
    WindowsScreenCaptureService screenCapture) : INavigationGuidanceCaptureService
{
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmExtendedFrameBounds = 9;
    private readonly IForegroundWindowIdentityProvider _foreground = foreground
        ?? throw new ArgumentNullException(nameof(foreground));
    private readonly WindowsScreenCaptureService _screenCapture = screenCapture
        ?? throw new ArgumentNullException(nameof(screenCapture));

    public Task<NavigationGuidanceCaptureFrame> CaptureAsync(
        CaptureScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(scope))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        ForegroundWindowIdentity identity = _foreground.GetCurrent()
            ?? throw new InvalidOperationException("The active application is unavailable.");
        if (identity.WindowHandle == nint.Zero)
        {
            throw new InvalidOperationException("The active application window is unavailable.");
        }

        OverlayRectangle region = scope == CaptureScope.FullDisplay
            ? GetDisplayBounds(identity.WindowHandle)
            : GetWindowBounds(identity.WindowHandle);
        CapturedScreenImage image = _screenCapture.Capture(region);
        byte[] digest = SHA256.HashData(image.EncodedBytes);
        string application = string.IsNullOrWhiteSpace(identity.ProcessName)
            ? $"process-{identity.ProcessId}"
            : identity.ProcessName;
        var target = new TargetIdentity(
            application,
            identity.WindowHandle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        ContextSnapshot context = ContextSnapshot.FromImageDigest(
            ContextSource.RegionCapture,
            target,
            digest,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        ContextExecutionInput input = ContextExecutionInput.FromImage(
            context,
            new ContextImagePayload(
                image.EncodedBytes,
                image.MediaType,
                image.PixelWidth,
                image.PixelHeight));
        return Task.FromResult(new NavigationGuidanceCaptureFrame(
            input,
            Convert.ToHexString(digest)));
    }

    private static OverlayRectangle GetWindowBounds(nint window)
    {
        if (DwmGetWindowAttribute(
                window,
                DwmExtendedFrameBounds,
                out NativeRectangle rectangle,
                Marshal.SizeOf<NativeRectangle>()) != 0 &&
            !GetWindowRect(window, out rectangle))
        {
            throw new InvalidOperationException("The active window bounds are unavailable.");
        }

        return ToOverlayRectangle(rectangle);
    }

    private static OverlayRectangle GetDisplayBounds(nint window)
    {
        nint monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            throw new InvalidOperationException("The active display bounds are unavailable.");
        }

        return ToOverlayRectangle(info.Monitor);
    }

    private static OverlayRectangle ToOverlayRectangle(NativeRectangle rectangle)
    {
        int width = rectangle.Right - rectangle.Left;
        int height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("The capture area is empty.");
        }

        return new OverlayRectangle(rectangle.Left, rectangle.Top, width, height);
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
    private struct MonitorInfo
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle WorkArea;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(
        nint window,
        int attribute,
        out NativeRectangle value,
        int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
