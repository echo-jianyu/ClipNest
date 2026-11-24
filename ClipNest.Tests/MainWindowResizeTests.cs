using System.Windows;
using ClipNest;

namespace ClipNest.Tests;

public sealed class MainWindowResizeTests
{
    [Fact]
    public void HorizontalDrag_ChangesOnlyWidth()
    {
        var size = MainWindow.CalculateResizeSize(
            startWidth: 400,
            startHeight: 400,
            startCursor: new Point(100, 100),
            currentCursor: new Point(160, 100),
            minimumWidth: 320,
            maximumWidth: 900,
            minimumHeight: 240,
            maximumHeight: 900);

        Assert.Equal(460, size.Width);
        Assert.Equal(400, size.Height);
    }

    [Fact]
    public void VerticalDrag_ChangesOnlyHeight()
    {
        var size = MainWindow.CalculateResizeSize(
            startWidth: 400,
            startHeight: 400,
            startCursor: new Point(100, 100),
            currentCursor: new Point(100, 160),
            minimumWidth: 320,
            maximumWidth: 900,
            minimumHeight: 240,
            maximumHeight: 900);

        Assert.Equal(400, size.Width);
        Assert.Equal(460, size.Height);
    }

    [Fact]
    public void DragResize_ClampsWidthAndHeightIndependently()
    {
        var minimum = MainWindow.CalculateResizeSize(
            400,
            400,
            new Point(100, 100),
            new Point(-100, -100),
            320,
            900,
            240,
            900);
        var maximum = MainWindow.CalculateResizeSize(
            400,
            400,
            new Point(100, 100),
            new Point(800, 800),
            320,
            900,
            240,
            900);

        Assert.Equal(new Size(320, 240), minimum);
        Assert.Equal(new Size(900, 900), maximum);
    }

    [Fact]
    public void WheelScale_PreservesCurrentAspectRatio()
    {
        var size = MainWindow.CalculateScaledSize(
            new Size(400, 600),
            1.1,
            320,
            900,
            240,
            900);

        Assert.Equal(440, size.Width, precision: 6);
        Assert.Equal(660, size.Height, precision: 6);
        Assert.Equal(400d / 600d, size.Width / size.Height, precision: 6);
    }

    [Fact]
    public void WheelScale_ClampsProportionallyAtMaximum()
    {
        var size = MainWindow.CalculateScaledSize(
            new Size(800, 400),
            2,
            320,
            900,
            240,
            900);

        Assert.Equal(900, size.Width, precision: 6);
        Assert.Equal(450, size.Height, precision: 6);
    }

    [Fact]
    public void WheelScale_ClampsProportionallyAtMinimum()
    {
        var size = MainWindow.CalculateScaledSize(
            new Size(400, 600),
            0.1,
            320,
            900,
            240,
            900);

        Assert.Equal(320, size.Width, precision: 6);
        Assert.Equal(480, size.Height, precision: 6);
    }
}
