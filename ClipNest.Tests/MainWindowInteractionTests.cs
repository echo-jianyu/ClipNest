using ClipNest.Models;
using System.Windows.Input;

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
    public void ImageCardCopy_IsImmediateWhenPreviewHasDedicatedButton()
    {
        var item = ClipItem.Create(ClipContent.FromImage([1], 2, 2));

        Assert.False(MainWindow.ShouldDelayCardCopy(item));
    }

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
}
