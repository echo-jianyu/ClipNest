using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
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
    public void SmallLandscapeImage_DoesNotGrowBeyondNaturalDimensions()
    {
        var item = ClipItem.Create(ClipContent.FromImage([1], 266, 125));

        Assert.True(AdaptiveCardPanel.ShouldSpanColumns(item, 210, 2));
        var width = AdaptiveCardPanel.CalculateCardWidth(item, 436);
        var height = AdaptiveCardPanel.CalculateCardHeight(item, width);

        Assert.Equal(278, width);
        Assert.Equal(137, height);
        Assert.Equal(item.PixelWidth, width - 12);
        Assert.Equal(item.PixelHeight, height - 12);
    }

    [Fact]
    public void ImageThatFitsOneColumn_DoesNotSpanOrStretchToColumnWidth()
    {
        var item = ClipItem.Create(ClipContent.FromImage([1], 180, 72));

        Assert.False(AdaptiveCardPanel.ShouldSpanColumns(item, 210, 2));
        Assert.Equal(192, AdaptiveCardPanel.CalculateCardWidth(item, 210));
    }

    [Fact]
    public void WideImageCard_SpansFromTheLeftEdge()
    {
        RunOnSta(() =>
        {
            var panel = new AdaptiveCardPanel();
            var card = new Border
            {
                DataContext = ClipItem.Create(ClipContent.FromImage([1], 266, 125))
            };
            panel.Children.Add(card);

            panel.Measure(new Size(450, 300));
            panel.Arrange(new Rect(0, 0, 450, 300));

            Assert.Equal(0, card.TranslatePoint(new Point(0, 0), panel).X);
            Assert.Equal(278, card.ActualWidth);
        });
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

    [Fact]
    public void TextHeight_RespectsConfiguredFontSize()
    {
        var small = ClipItem.Create(ClipContent.FromText(new string('a', 60)));
        var large = ClipItem.Create(ClipContent.FromText(new string('a', 60)));
        small.ApplyDisplaySettings(40, 11);
        large.ApplyDisplaySettings(40, 18);

        Assert.True(
            AdaptiveCardPanel.CalculateCardHeight(large, 210)
            > AdaptiveCardPanel.CalculateCardHeight(small, 210));
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
