using System.Windows;
using ClipNest;

namespace ClipNest.Tests;

public sealed class MainWindowResizeTests
{
    [Fact]
    public void HorizontalDrag_UsesAbsoluteMovementFromDragStart()
    {
        var width = MainWindow.CalculateResizeWidth(
            startWidth: 450,
            startCursor: new Point(100, 100),
            currentCursor: new Point(160, 130),
            aspectRatio: 1.5,
            minimumWidth: 450,
            maximumWidth: 900);

        Assert.Equal(510, width);
    }

    [Fact]
    public void VerticalDrag_ChangesWidthToPreserveAspectRatio()
    {
        var width = MainWindow.CalculateResizeWidth(
            startWidth: 450,
            startCursor: new Point(100, 100),
            currentCursor: new Point(110, 160),
            aspectRatio: 1.5,
            minimumWidth: 450,
            maximumWidth: 900);

        Assert.Equal(540, width);
    }

    [Fact]
    public void ResizeWidth_IsClampedToConfiguredBounds()
    {
        Assert.Equal(
            450,
            MainWindow.CalculateResizeWidth(
                450,
                new Point(100, 100),
                new Point(20, 40),
                1.5,
                450,
                900));
        Assert.Equal(
            900,
            MainWindow.CalculateResizeWidth(
                450,
                new Point(100, 100),
                new Point(700, 100),
                1.5,
                450,
                900));
    }
}
