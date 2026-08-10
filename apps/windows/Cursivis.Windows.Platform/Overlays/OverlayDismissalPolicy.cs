namespace Cursivis.Windows.Platform.Overlays;

public sealed record OverlayDismissalContext(
    bool IsResultPanelVisible,
    OverlayRectangle ResultPanel,
    IReadOnlyList<OverlayRectangle> ChildOverlays,
    bool IsDraggingOrResizing);

public static class OverlayDismissalPolicy
{
    public static bool ShouldDismiss(
        OverlayPoint pointer,
        OverlayDismissalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsResultPanelVisible || context.IsDraggingOrResizing)
        {
            return false;
        }

        if (Contains(context.ResultPanel, pointer))
        {
            return false;
        }

        return !context.ChildOverlays.Any(child => Contains(child, pointer));
    }

    private static bool Contains(OverlayRectangle rectangle, OverlayPoint point) =>
        point.X >= rectangle.X &&
        point.X < rectangle.Right &&
        point.Y >= rectangle.Y &&
        point.Y < rectangle.Bottom;
}
