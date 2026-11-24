using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using ClipNest.Controls;
using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class AdaptiveCardPanelTests
{
    [Fact]
    public void WideLayout_StillUsesOneFullWidthColumn()
    {
        RunOnSta(() =>
        {
            var panel = new AdaptiveCardPanel();
            var first = CreateCard(ClipItem.Create(ClipContent.FromText("first")), 100);
            var second = CreateCard(ClipItem.Create(ClipContent.FromText("second")), 40);
            var third = CreateCard(ClipItem.Create(ClipContent.FromText("third")), 40);
            panel.Children.Add(first);
            panel.Children.Add(second);
            panel.Children.Add(third);

            Arrange(panel, 600);

            Assert.Equal(600, first.ActualWidth);
            Assert.Equal(600, second.ActualWidth);
            Assert.Equal(600, third.ActualWidth);
            Assert.Equal(0, first.TranslatePoint(new Point(0, 0), panel).X);
            Assert.Equal(0, second.TranslatePoint(new Point(0, 0), panel).X);
            Assert.Equal(0, third.TranslatePoint(new Point(0, 0), panel).X);
            Assert.Equal(107, second.TranslatePoint(new Point(0, 0), panel).Y);
            Assert.Equal(154, third.TranslatePoint(new Point(0, 0), panel).Y);
        });
    }

    [Fact]
    public void ShortTextCard_ClampsToCompactMinimumHeight()
    {
        RunOnSta(() =>
        {
            var panel = new AdaptiveCardPanel();
            var card = new Border
            {
                DataContext = ClipItem.Create(ClipContent.FromText("short")),
                Child = new TextBlock
                {
                    Text = "short",
                    FontSize = ClipItem.DefaultDisplayFontSize
                }
            };
            panel.Children.Add(card);

            Arrange(panel, 450);

            Assert.Equal(40, card.ActualHeight);
        });
    }

    [Fact]
    public void WrappedTextCard_UsesMeasuredContentHeight()
    {
        RunOnSta(() =>
        {
            var item = ClipItem.Create(ClipContent.FromText(new string('字', 120)));
            var panel = new AdaptiveCardPanel();
            var card = new Border
            {
                DataContext = item,
                Child = new TextBlock
                {
                    Text = item.DisplayText,
                    Width = 120,
                    FontSize = item.DisplayFontSize,
                    LineHeight = item.DisplayLineHeight,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            panel.Children.Add(card);

            Arrange(panel, 280);

            Assert.True(card.ActualHeight > 40);
        });
    }

    [Fact]
    public void NarrowImageCard_FrameFillsTheWholeColumn()
    {
        RunOnSta(() =>
        {
            var panel = new AdaptiveCardPanel();
            var card = new Border
            {
                DataContext = ClipItem.Create(ClipContent.FromImage([1], 180, 72)),
                Child = new Border
                {
                    Width = 100,
                    Height = 50
                }
            };
            panel.Children.Add(card);

            Arrange(panel, 450);

            Assert.Equal(450, card.ActualWidth);
        });
    }

    [Fact]
    public void TallImageCard_HeightIsBounded()
    {
        RunOnSta(() =>
        {
            var panel = new AdaptiveCardPanel();
            var card = new Border
            {
                DataContext = ClipItem.Create(ClipContent.FromImage([1], 600, 3000)),
                Child = new Border
                {
                    Height = 1000
                }
            };
            panel.Children.Add(card);

            Arrange(panel, 450);

            Assert.Equal(220, card.ActualHeight);
        });
    }

    private static Border CreateCard(ClipItem item, double height)
    {
        return new Border
        {
            DataContext = item,
            Height = height
        };
    }

    private static void Arrange(AdaptiveCardPanel panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
        panel.UpdateLayout();
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
