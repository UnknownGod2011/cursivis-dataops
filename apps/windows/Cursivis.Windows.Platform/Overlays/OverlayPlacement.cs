namespace Cursivis.Windows.Platform.Overlays;

public readonly record struct OverlayPoint(int X, int Y);

public readonly record struct OverlaySize
{
    public OverlaySize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }
}

public readonly record struct OverlayRectangle
{
    public OverlayRectangle(int x, int y, int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => checked(X + Width);

    public int Bottom => checked(Y + Height);
}

/// <summary>
/// Describes one logical, client-coordinate portion of a shaped overlay HWND.
/// The platform layer scales the shape for the window's current monitor.
/// </summary>
public readonly record struct OverlayRegionShape
{
    public OverlayRegionShape(
        int x,
        int y,
        int width,
        int height,
        int cornerRadius = 0,
        bool isEllipse = false)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (cornerRadius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cornerRadius));
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
        CornerRadius = cornerRadius;
        IsEllipse = isEllipse;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public int CornerRadius { get; }

    public bool IsEllipse { get; }
}

public static class OverlayPlacementCalculator
{
    public static OverlayRectangle PlaceNearPoint(
        OverlayPoint anchor,
        OverlaySize desiredSize,
        OverlayRectangle workArea,
        int gap = 14,
        int margin = 12)
    {
        if (gap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gap));
        }

        if (margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }

        int availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        int availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        int width = Math.Min(desiredSize.Width, availableWidth);
        int height = Math.Min(desiredSize.Height, availableHeight);
        int minimumX = workArea.X + margin;
        int minimumY = workArea.Y + margin;
        int maximumX = workArea.Right - width - margin;
        int maximumY = workArea.Bottom - height - margin;

        int preferredX = anchor.X + gap;
        int preferredY = anchor.Y + gap;
        if (preferredX + width > workArea.Right - margin)
        {
            preferredX = anchor.X - width - gap;
        }

        if (preferredY + height > workArea.Bottom - margin)
        {
            preferredY = anchor.Y - height - gap;
        }

        return new(
            Math.Clamp(preferredX, minimumX, maximumX),
            Math.Clamp(preferredY, minimumY, maximumY),
            width,
            height);
    }

    public static OverlayRectangle Clamp(
        OverlayRectangle requested,
        OverlayRectangle workArea,
        int margin = 12)
    {
        return PlaceNearPoint(
            new OverlayPoint(requested.X - 14, requested.Y - 14),
            new OverlaySize(requested.Width, requested.Height),
            workArea,
            gap: 14,
            margin);
    }
}

public static class OverlayResizeCalculator
{
    public static OverlayRectangle Clamp(
        OverlayRectangle requested,
        OverlayRectangle previous,
        OverlayRectangle workArea,
        OverlaySize minimumSize,
        int margin = 12)
    {
        if (margin < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(margin));
        }

        int availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        int availableHeight = Math.Max(1, workArea.Height - (margin * 2));
        int minimumWidth = Math.Min(minimumSize.Width, availableWidth);
        int minimumHeight = Math.Min(minimumSize.Height, availableHeight);
        int width = Math.Clamp(requested.Width, minimumWidth, availableWidth);
        int height = Math.Clamp(requested.Height, minimumHeight, availableHeight);
        int x = requested.X;
        int y = requested.Y;

        if (width != requested.Width && requested.X != previous.X)
        {
            x += requested.Width - width;
        }

        if (height != requested.Height && requested.Y != previous.Y)
        {
            y += requested.Height - height;
        }

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
}
