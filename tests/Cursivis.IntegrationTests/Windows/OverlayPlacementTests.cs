using Cursivis.Windows.Platform.Overlays;

namespace Cursivis.IntegrationTests.Windows;

public sealed class OverlayPlacementTests
{
    [Fact]
    public void PlaceNearPoint_UsesLowerRightWhenThereIsRoom()
    {
        OverlayRectangle result = OverlayPlacementCalculator.PlaceNearPoint(
            new OverlayPoint(400, 300),
            new OverlaySize(680, 420),
            new OverlayRectangle(0, 0, 1920, 1040));

        Assert.Equal(new OverlayRectangle(414, 314, 680, 420), result);
    }

    [Fact]
    public void PlaceNearPoint_FlipsAndClampsInsideNegativeCoordinateMonitor()
    {
        var monitor = new OverlayRectangle(-2560, -120, 2560, 1440);

        OverlayRectangle result = OverlayPlacementCalculator.PlaceNearPoint(
            new OverlayPoint(-20, 1200),
            new OverlaySize(760, 460),
            monitor);

        Assert.True(result.X >= monitor.X + 12);
        Assert.True(result.Y >= monitor.Y + 12);
        Assert.True(result.Right <= monitor.Right - 12);
        Assert.True(result.Bottom <= monitor.Bottom - 12);
    }

    [Fact]
    public void PlaceNearPoint_ShrinksOversizedOverlayToMonitorWorkArea()
    {
        OverlayRectangle result = OverlayPlacementCalculator.PlaceNearPoint(
            new OverlayPoint(0, 0),
            new OverlaySize(1600, 1000),
            new OverlayRectangle(0, 0, 800, 600));

        Assert.Equal(new OverlayRectangle(12, 12, 776, 576), result);
    }

    [Fact]
    public void ShouldDismiss_ClickOutsidePanelAndChildren_ReturnsTrue()
    {
        var context = new OverlayDismissalContext(
            IsResultPanelVisible: true,
            new OverlayRectangle(100, 100, 600, 400),
            [new OverlayRectangle(720, 100, 300, 300)],
            IsDraggingOrResizing: false);

        Assert.True(OverlayDismissalPolicy.ShouldDismiss(new OverlayPoint(20, 20), context));
    }

    [Theory]
    [InlineData(120, 120)]
    [InlineData(800, 180)]
    public void ShouldDismiss_ClickInsidePanelOrChildOverlay_ReturnsFalse(int x, int y)
    {
        var context = new OverlayDismissalContext(
            IsResultPanelVisible: true,
            new OverlayRectangle(100, 100, 600, 400),
            [new OverlayRectangle(720, 100, 300, 300)],
            IsDraggingOrResizing: false);

        Assert.False(OverlayDismissalPolicy.ShouldDismiss(new OverlayPoint(x, y), context));
    }

    [Fact]
    public void ShouldDismiss_DuringDragOrResize_ReturnsFalse()
    {
        var context = new OverlayDismissalContext(
            IsResultPanelVisible: true,
            new OverlayRectangle(100, 100, 600, 400),
            [],
            IsDraggingOrResizing: true);

        Assert.False(OverlayDismissalPolicy.ShouldDismiss(new OverlayPoint(20, 20), context));
    }

    [Fact]
    public void Resize_FromBottomRight_EnforcesMinimumAndKeepsOrigin()
    {
        var previous = new OverlayRectangle(300, 180, 612, 312);

        OverlayRectangle result = OverlayResizeCalculator.Clamp(
            new OverlayRectangle(300, 180, 420, 160),
            previous,
            new OverlayRectangle(0, 0, 1920, 1040),
            new OverlaySize(516, 232));

        Assert.Equal(new OverlayRectangle(300, 180, 516, 232), result);
    }

    [Fact]
    public void Resize_FromTopLeft_EnforcesMinimumAndKeepsOppositeEdges()
    {
        var previous = new OverlayRectangle(300, 180, 612, 312);

        OverlayRectangle result = OverlayResizeCalculator.Clamp(
            new OverlayRectangle(520, 330, 392, 162),
            previous,
            new OverlayRectangle(0, 0, 1920, 1040),
            new OverlaySize(516, 232));

        Assert.Equal(396, result.X);
        Assert.Equal(260, result.Y);
        Assert.Equal(912, result.Right);
        Assert.Equal(492, result.Bottom);
    }

    [Fact]
    public void Resize_ClampsOversizedResultToMonitorWorkArea()
    {
        var previous = new OverlayRectangle(-1600, 40, 612, 312);

        OverlayRectangle result = OverlayResizeCalculator.Clamp(
            new OverlayRectangle(-1800, -100, 2200, 1400),
            previous,
            new OverlayRectangle(-1920, 0, 1920, 1040),
            new OverlaySize(516, 232));

        Assert.Equal(new OverlayRectangle(-1908, 12, 1896, 1016), result);
    }
}
