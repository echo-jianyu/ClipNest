using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class MainWindowInteractionTests
{
    [Fact]
    public void TextCardCopy_IsImmediate()
    {
        var item = ClipItem.Create(ClipContent.FromText("text"));

        Assert.False(MainWindow.ShouldDelayCardCopy(item));
    }

    [Fact]
    public void ImageCardCopy_WaitsForDoubleClickDecision()
    {
        var item = ClipItem.Create(ClipContent.FromImage([1], 2, 2));

        Assert.True(MainWindow.ShouldDelayCardCopy(item));
    }
}
