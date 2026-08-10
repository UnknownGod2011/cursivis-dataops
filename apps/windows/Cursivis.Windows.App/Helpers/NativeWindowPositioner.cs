using System.Runtime.InteropServices;
using Cursivis.Windows.Platform.Overlays;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Cursivis.Windows.App.Helpers;

internal static class NativeWindowPositioner
{
    private const uint MonitorDefaultToNearest = 2;

    public static (OverlayRectangle Monitor, OverlayPoint Cursor) GetCursorMonitor()
    {
        if (!GetCursorPos(out Point cursor))
        {
            cursor = new Point { X = 0, Y = 0 };
        }

        nint monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        MonitorInfo info = MonitorInfo.Create();
        OverlayRectangle bounds = monitor != nint.Zero && GetMonitorInfo(monitor, ref info)
            ? new OverlayRectangle(
                info.Monitor.Left,
                info.Monitor.Top,
                Math.Max(1, info.Monitor.Right - info.Monitor.Left),
                Math.Max(1, info.Monitor.Bottom - info.Monitor.Top))
            : new OverlayRectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
        return (bounds, new OverlayPoint(cursor.X, cursor.Y));
    }

    public static OverlayRectangle GetNearCursorPlacement(
        nint windowHandle,
        int logicalWidth,
        int logicalHeight)
    {
        if (!GetCursorPos(out Point cursor))
        {
            cursor = new Point { X = 0, Y = 0 };
        }

        nint monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        double scale = GetMonitorScale(monitor, windowHandle);
        var desired = new OverlaySize(
            Math.Max(1, (int)Math.Round(logicalWidth * scale)),
            Math.Max(1, (int)Math.Round(logicalHeight * scale)));
        return OverlayPlacementCalculator.PlaceNearPoint(
            new OverlayPoint(cursor.X, cursor.Y),
            desired,
            GetWorkArea(monitor));
    }

    public static OverlayRectangle GetRestoredPlacement(
        nint windowHandle,
        int logicalWidth,
        int logicalHeight,
        OverlayPoint restoredPoint)
    {
        var nativePoint = new Point { X = restoredPoint.X, Y = restoredPoint.Y };
        nint monitor = MonitorFromPoint(nativePoint, MonitorDefaultToNearest);
        double scale = GetMonitorScale(monitor, windowHandle);
        var requested = new OverlayRectangle(
            restoredPoint.X,
            restoredPoint.Y,
            Math.Max(1, (int)Math.Round(logicalWidth * scale)),
            Math.Max(1, (int)Math.Round(logicalHeight * scale)));
        return OverlayPlacementCalculator.Clamp(requested, GetWorkArea(monitor));
    }

    public static OverlayRectangle GetAdjacentPlacement(
        nint windowHandle,
        int logicalWidth,
        int logicalHeight,
        OverlayRectangle anchor,
        int gap = 12,
        int margin = 12)
    {
        var anchorCenter = new Point
        {
            X = anchor.X + (anchor.Width / 2),
            Y = anchor.Y + (anchor.Height / 2),
        };
        nint monitor = MonitorFromPoint(anchorCenter, MonitorDefaultToNearest);
        double scale = GetMonitorScale(monitor, windowHandle);
        OverlayRectangle workArea = GetWorkArea(monitor);
        int width = Math.Min(
            Math.Max(1, (int)Math.Round(logicalWidth * scale)),
            Math.Max(1, workArea.Width - (margin * 2)));
        int height = Math.Min(
            Math.Max(1, (int)Math.Round(logicalHeight * scale)),
            Math.Max(1, workArea.Height - (margin * 2)));

        int x = anchor.Right + gap;
        if (x + width > workArea.Right - margin)
        {
            x = anchor.X - width - gap;
        }

        int y = anchor.Y + ((anchor.Height - height) / 2);
        x = Math.Clamp(
            x,
            workArea.X + margin,
            Math.Max(workArea.X + margin, workArea.Right - width - margin));
        y = Math.Clamp(
            y,
            workArea.Y + margin,
            Math.Max(workArea.Y + margin, workArea.Bottom - height - margin));
        return new OverlayRectangle(x, y, width, height);
    }

    public static OverlayRectangle ClampCurrentPlacement(
        nint windowHandle,
        OverlayRectangle current)
    {
        nint monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        return OverlayPlacementCalculator.Clamp(current, GetWorkArea(monitor));
    }

    public static OverlaySize GetLogicalSize(nint windowHandle, OverlayRectangle physicalSize)
    {
        nint monitor = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        double scale = GetMonitorScale(monitor, windowHandle);
        return new OverlaySize(
            Math.Max(1, (int)Math.Round(physicalSize.Width / scale)),
            Math.Max(1, (int)Math.Round(physicalSize.Height / scale)));
    }

    public static void FitAndCenter(AppWindow window, int desiredWidth, int desiredHeight)
    {
        DisplayArea display = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        RectInt32 workArea = display.WorkArea;
        const int margin = 24;
        int width = Math.Min(desiredWidth, Math.Max(320, workArea.Width - (margin * 2)));
        int height = Math.Min(desiredHeight, Math.Max(320, workArea.Height - (margin * 2)));
        int left = workArea.X + Math.Max(margin, (workArea.Width - width) / 2);
        int top = workArea.Y + Math.Max(margin, (workArea.Height - height) / 2);
        window.MoveAndResize(new RectInt32(left, top, width, height));
    }

    private static OverlayRectangle GetWorkArea(nint monitor)
    {
        MonitorInfo info = MonitorInfo.Create();
        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref info))
        {
            return new OverlayRectangle(
                info.WorkArea.Left,
                info.WorkArea.Top,
                Math.Max(1, info.WorkArea.Right - info.WorkArea.Left),
                Math.Max(1, info.WorkArea.Bottom - info.WorkArea.Top));
        }

        return new OverlayRectangle(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
    }

    private static double GetMonitorScale(nint monitor, nint windowHandle)
    {
        try
        {
            if (monitor != nint.Zero &&
                GetScaleFactorForMonitor(monitor, out int scalePercent) == 0 &&
                scalePercent is >= 100 and <= 450)
            {
                return scalePercent / 100d;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        uint windowDpi = GetDpiForWindow(windowHandle);
        return windowDpi == 0 ? 1d : Math.Clamp(windowDpi / 96d, 1d, 4d);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public Rect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;

        public static MonitorInfo Create() => new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(nint monitor, out int scalePercent);
}
