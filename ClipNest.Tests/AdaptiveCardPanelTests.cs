using ClipNest.Controls;
using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class AdaptiveCardPanelTests
{
    [Theory]
    [InlineData(280, 1)]
    [InlineData(450, 2)]
    [InlineData(720, 3)]
    public void ColumnCount_AdaptsToAvailableWidth(int width, int expected)
    {
        Assert.Equal(expected, AdaptiveCardPanel.GetColumnCount(width));
    }

    [Fact]
    public void LongText_SpansColumnsWhenNotPinned()
    {
        var item = ClipItem.Create(ClipContent.FromText(new string('a', 120)));

        Assert.True(AdaptiveCardPanel.ShouldSpanColumns(item, 210, 2));

        item.Pinned = true;
        Assert.False(AdaptiveCardPanel.ShouldSpanColumns(item, 210, 2));
    }

    [Fact]
    public void LandscapeAndPortraitImagesChooseDifferentSpans()
    {
        var landscape = ClipItem.Create(ClipContent.FromImage([1], 1600, 600));
        var portrait = ClipItem.Create(ClipContent.FromImage([1], 600, 1600));

        Assert.True(AdaptiveCardPanel.ShouldSpanColumns(landscape, 210, 2));
        Assert.False(AdaptiveCardPanel.ShouldSpanColumns(portrait, 210, 2));
    }

    [Fact]
    public void ImageHeight_UsesAspectRatioAndStaysBounded()
    {
        var landscape = ClipItem.Create(ClipContent.FromImage([1], 1600, 600));
        var portrait = ClipItem.Create(ClipContent.FromImage([1], 600, 1600));

        var landscapeHeight = AdaptiveCardPanel.CalculateCardHeight(landscape, 420);
        var portraitHeight = AdaptiveCardPanel.CalculateCardHeight(portrait, 210);

        Assert.InRange(landscapeHeight, 96, 250);
        Assert.InRange(portraitHeight, 96, 250);
        Assert.True(portraitHeight > landscapeHeight);
    }

    [Fact]
    public void TextHeight_GrowsWithPreviewLength()
    {
        var shortText = ClipItem.Create(ClipContent.FromText("short"));
        var mediumText = ClipItem.Create(ClipContent.FromText(
            string.Join(" ", Enumerable.Repeat("medium", 10))));

        var shortHeight = AdaptiveCardPanel.CalculateCardHeight(shortText, 210);
        var mediumHeight = AdaptiveCardPanel.CalculateCardHeight(mediumText, 210);

        Assert.True(mediumHeight > shortHeight);
    }
}
