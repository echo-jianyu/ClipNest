using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipNest.Tests;

public sealed class MainWindowInteractionTests
{
    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D5, 4)]
    [InlineData(Key.NumPad1, 0)]
    [InlineData(Key.NumPad5, 4)]
    public void HistoryShortcuts_SupportTopRowAndNumpad(Key key, int expectedIndex)
    {
        var found = MainWindow.TryGetHistoryShortcutIndex(key, out var index);

        Assert.True(found);
        Assert.Equal(expectedIndex, index);
    }

    [Fact]
    public void HistoryShortcuts_IgnoreUnknownKeys()
    {
        Assert.False(MainWindow.TryGetHistoryShortcutIndex(Key.A, out _));
    }

    [Fact]
    public void ImagePreviewZoom_KeepsCursorAnchorStationary()
    {
        var anchor = new Point(180, 120);
        var matrix = MainWindow.CalculateImagePreviewZoomMatrix(
            Matrix.Identity,
            anchor,
            nextScale: 2);

        var transformedAnchor = matrix.Transform(anchor);

        Assert.Equal(anchor.X, transformedAnchor.X, precision: 6);
        Assert.Equal(anchor.Y, transformedAnchor.Y, precision: 6);
    }

    [Fact]
    public void ImagePreviewZoom_UsesCurrentTransformWhenChangingScale()
    {
        var anchor = new Point(180, 120);
        var initial = MainWindow.CalculateImagePreviewZoomMatrix(
            Matrix.Identity,
            anchor,
            nextScale: 2);
        var zoomed = MainWindow.CalculateImagePreviewZoomMatrix(
            initial,
            anchor,
            nextScale: 4);

        var transformedAnchor = zoomed.Transform(anchor);

        Assert.Equal(anchor.X, transformedAnchor.X, precision: 6);
        Assert.Equal(anchor.Y, transformedAnchor.Y, precision: 6);
        Assert.Equal(4, zoomed.M11, precision: 6);
    }

    [Fact]
    public void ImagePreviewPan_AppliesDragDeltaToCurrentOffset()
    {
        var current = new Matrix(2, 0, 0, 2, -120, -80);
        var next = new Matrix(
            current.M11,
            current.M12,
            current.M21,
            current.M22,
            current.OffsetX + 35,
            current.OffsetY - 20);

        Assert.Equal(-85, next.OffsetX, precision: 6);
        Assert.Equal(-100, next.OffsetY, precision: 6);
    }

    [Fact]
    public void ImagePreviewPan_ConstrainsImageToViewportEdges()
    {
        var viewport = new Size(400, 400);
        var content = new Size(400, 200);
        var zoomed = new Matrix(2, 0, 0, 2, -900, -500);

        var constrained = MainWindow.ConstrainImagePreviewPanMatrix(
            zoomed,
            viewport,
            content);

        Assert.Equal(-400, constrained.OffsetX, precision: 6);
        Assert.Equal(0, constrained.OffsetY, precision: 6);
    }

    [Fact]
    public void ImagePreviewPan_AllowsSmallContentToMoveInsideViewport()
    {
        var viewport = new Size(400, 400);
        var content = new Size(400, 200);
        var reduced = new Matrix(0.2, 0, 0, 0.2, 300, 300);

        var constrained = MainWindow.ConstrainImagePreviewPanMatrix(
            reduced,
            viewport,
            content);

        Assert.Equal(300, constrained.OffsetX, precision: 6);
        Assert.Equal(300, constrained.OffsetY, precision: 6);
    }

    [Fact]
    public void ImagePreviewPan_TallImageAtHighZoomCanRevealHiddenLeftSide()
    {
        var viewport = new Size(336, 322);
        var content = new Size(322 * 520d / 745d, 322);
        var zoomed = new Matrix(4.05, 0, 0, 4.05, -512, -600);
        var draggedRight = new Matrix(
            zoomed.M11,
            zoomed.M12,
            zoomed.M21,
            zoomed.M22,
            zoomed.OffsetX + 100,
            zoomed.OffsetY);

        var constrained = MainWindow.ConstrainImagePreviewPanMatrix(
            draggedRight,
            viewport,
            content);
        var draggedFullyRight = MainWindow.ConstrainImagePreviewPanMatrix(
            new Matrix(
                zoomed.M11,
                zoomed.M12,
                zoomed.M21,
                zoomed.M22,
                zoomed.OffsetX + 1000,
                zoomed.OffsetY),
            viewport,
            content);

        Assert.Equal(-412, constrained.OffsetX, precision: 6);
        Assert.Equal(-600, constrained.OffsetY, precision: 6);
        Assert.Equal(0, draggedFullyRight.OffsetX, precision: 6);
    }

}
