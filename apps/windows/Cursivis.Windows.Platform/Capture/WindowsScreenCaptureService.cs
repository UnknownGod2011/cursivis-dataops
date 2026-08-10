using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Cursivis.Application.Context;
using Cursivis.Windows.Platform.Overlays;

namespace Cursivis.Windows.Platform.Capture;

public sealed record CapturedScreenImage(
    byte[] EncodedBytes,
    string MediaType,
    int PixelWidth,
    int PixelHeight);

public sealed record CapturedScreenPixel(
    CapturedScreenImage Image,
    DetectedColor Color);

public sealed class WindowsScreenCaptureService
{
    private const int MaximumDimension = 2048;

    public CapturedScreenImage Capture(OverlayRectangle requestedRegion)
    {
        OverlayRectangle region = ClampToVirtualScreen(requestedRegion);
        _ = DwmFlush();

        using var source = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(
                region.X,
                region.Y,
                0,
                0,
                new Size(region.Width, region.Height),
                CopyPixelOperation.SourceCopy);
        }

        using Bitmap encodedSource = ResizeIfNeeded(source);
        using var png = new MemoryStream();
        encodedSource.Save(png, ImageFormat.Png);
        if (png.Length <= ContextImagePayload.MaximumEncodedBytes)
        {
            return new CapturedScreenImage(
                png.ToArray(),
                "image/png",
                encodedSource.Width,
                encodedSource.Height);
        }

        using var jpeg = new MemoryStream();
        ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders()
            .Single(static candidate => candidate.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        encodedSource.Save(jpeg, codec, parameters);
        if (jpeg.Length > ContextImagePayload.MaximumEncodedBytes)
        {
            throw new InvalidOperationException("The selected screen region is too large to analyze safely.");
        }

        return new CapturedScreenImage(
            jpeg.ToArray(),
            "image/jpeg",
            encodedSource.Width,
            encodedSource.Height);
    }

    public CapturedScreenPixel CapturePixel(ScreenAnchor point)
    {
        OverlayRectangle virtualScreen = GetVirtualScreen();
        int x = Math.Clamp(point.X, virtualScreen.X, virtualScreen.Right - 1);
        int y = Math.Clamp(point.Y, virtualScreen.Y, virtualScreen.Bottom - 1);
        _ = DwmFlush();

        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(x, y, 0, 0, new Size(1, 1), CopyPixelOperation.SourceCopy);
        }

        Color color = bitmap.GetPixel(0, 0);
        DetectedColor detected = new(
            color.R,
            color.G,
            color.B,
            $"#{color.R:X2}{color.G:X2}{color.B:X2}",
            ApproximateColorNamer.Find(color.R, color.G, color.B));
        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        return new CapturedScreenPixel(
            new CapturedScreenImage(png.ToArray(), "image/png", 1, 1),
            detected);
    }

    private static Bitmap ResizeIfNeeded(Bitmap source)
    {
        if (source.Width <= MaximumDimension && source.Height <= MaximumDimension)
        {
            return source.Clone(
                new Rectangle(0, 0, source.Width, source.Height),
                PixelFormat.Format32bppArgb);
        }

        double scale = Math.Min(
            MaximumDimension / (double)source.Width,
            MaximumDimension / (double)source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(resized);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }

    private static OverlayRectangle ClampToVirtualScreen(OverlayRectangle requested)
    {
        OverlayRectangle screen = GetVirtualScreen();
        int left = Math.Max(requested.X, screen.X);
        int top = Math.Max(requested.Y, screen.Y);
        int right = Math.Min(requested.Right, screen.Right);
        int bottom = Math.Min(requested.Bottom, screen.Bottom);
        if (right <= left || bottom <= top)
        {
            throw new ArgumentException("The selected region is outside the visible desktop.", nameof(requested));
        }

        return new OverlayRectangle(left, top, right - left, bottom - top);
    }

    private static OverlayRectangle GetVirtualScreen() => new(
        GetSystemMetrics(76),
        GetSystemMetrics(77),
        Math.Max(1, GetSystemMetrics(78)),
        Math.Max(1, GetSystemMetrics(79)));

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
