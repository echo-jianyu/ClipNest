using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClipNest.Models;

namespace ClipNest.Controls;

public sealed class AdaptiveCardPanel : Panel
{
    private const double Gap = 7;
    private readonly List<CardPlacement> _placements = [];
    private readonly Dictionary<UIElement, ClipItem> _observedItems = [];

    internal static int GetColumnCount(double width)
    {
        if (width >= 720)
        {
            return 3;
        }

        return width >= 300 ? 2 : 1;
    }

    internal static bool ShouldSpanColumns(
        ClipItem item,
        double columnWidth,
        int columnCount)
    {
        if (item.Pinned || columnCount <= 1)
        {
            return false;
        }

        if (item.IsImage)
        {
            return item.PixelHeight > 0
                && (double)item.PixelWidth / item.PixelHeight >= 1.6;
        }

        var textLength = NormalizeText(item.PlainText).Length;
        var threshold = columnWidth >= 260 ? 90 : 58;
        return textLength > threshold;
    }

    internal static double CalculateCardHeight(ClipItem item, double cardWidth)
    {
        if (item.IsImage)
        {
            var aspect = item.PixelWidth > 0 && item.PixelHeight > 0
                ? (double)item.PixelWidth / item.PixelHeight
                : 1.5;
            return Math.Min(250, Math.Max(96, (cardWidth / aspect) + 12));
        }

        var charactersPerLine = Math.Max(
            8,
            (int)(cardWidth / Math.Max(1, item.DisplayFontSize * 1.04)));
        var textLength = Math.Max(1, item.DisplayText.Length);
        var lineCount = Math.Min(
            6,
            Math.Max(1, (int)Math.Ceiling((double)textLength / charactersPerLine)));
        return Math.Min(
            140,
            Math.Max(
                48,
                25 + (lineCount * Math.Max(15, item.DisplayLineHeight))));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _placements.Clear();
        if (InternalChildren.Count == 0)
        {
            return new Size();
        }

        var width = double.IsNaN(availableSize.Width)
            || double.IsInfinity(availableSize.Width)
            || availableSize.Width <= 0
            ? 450
            : availableSize.Width;
        var columnCount = GetColumnCount(width);
        var columnWidth = Math.Max(
            1,
            (width - (Gap * (columnCount - 1))) / columnCount);
        var columnHeights = new double[columnCount];

        foreach (UIElement child in InternalChildren)
        {
            var item = (child as FrameworkElement)?.DataContext as ClipItem;
            var spansColumns = item is not null
                && ShouldSpanColumns(item, columnWidth, columnCount);
            var cardWidth = spansColumns ? width : columnWidth;
            var cardHeight = item is not null
                ? CalculateCardHeight(item, cardWidth)
                : 48;

            int column;
            double top;
            if (spansColumns)
            {
                column = 0;
                top = columnHeights.Max();
            }
            else
            {
                column = FindShortestColumn(columnHeights);
                top = columnHeights[column];
            }

            var left = column * (columnWidth + Gap);
            child.Measure(new Size(cardWidth, cardHeight));
            _placements.Add(new CardPlacement(child, left, top, cardWidth, cardHeight));

            if (spansColumns)
            {
                for (var index = 0; index < columnCount; index++)
                {
                    columnHeights[index] = top + cardHeight + Gap;
                }
            }
            else
            {
                columnHeights[column] = top + cardHeight + Gap;
            }
        }

        var height = Math.Max(0, columnHeights.Max() - Gap);
        return new Size(width, height);
    }

    protected override void OnVisualChildrenChanged(
        DependencyObject visualAdded,
        DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);

        if (visualRemoved is FrameworkElement removedElement)
        {
            StopObserving(removedElement);
            removedElement.DataContextChanged -= Element_DataContextChanged;
        }

        if (visualAdded is FrameworkElement addedElement)
        {
            addedElement.DataContextChanged += Element_DataContextChanged;
            Observe(addedElement);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var placement in _placements)
        {
            placement.Child.Arrange(new Rect(
                placement.Left,
                placement.Top,
                placement.Width,
                placement.Height));
        }

        return finalSize;
    }

    private static int FindShortestColumn(double[] columnHeights)
    {
        var shortest = 0;
        for (var index = 1; index < columnHeights.Length; index++)
        {
            if (columnHeights[index] < columnHeights[shortest])
            {
                shortest = index;
            }
        }

        return shortest;
    }

    private void Element_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (sender is FrameworkElement element)
        {
            StopObserving(element);
            Observe(element);
        }
    }

    private void Observe(FrameworkElement element)
    {
        if (element.DataContext is not ClipItem item)
        {
            return;
        }

        item.PropertyChanged += Item_PropertyChanged;
        _observedItems[element] = item;
    }

    private void StopObserving(FrameworkElement element)
    {
        if (_observedItems.TryGetValue(element, out var item))
        {
            item.PropertyChanged -= Item_PropertyChanged;
            _observedItems.Remove(element);
        }
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName is nameof(ClipItem.PixelWidth)
            or nameof(ClipItem.PixelHeight)
            or nameof(ClipItem.DisplayText)
            or nameof(ClipItem.DisplayFontSize)
            or nameof(ClipItem.PlainText)
            or nameof(ClipItem.Pinned)
            or nameof(ClipItem.IsImage))
        {
            InvalidateMeasure();
        }
    }

    private static string NormalizeText(string text)
    {
        return string.Join(
            " ",
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class CardPlacement
    {
        public CardPlacement(
            UIElement child,
            double left,
            double top,
            double width,
            double height)
        {
            Child = child;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        public UIElement Child { get; }

        public double Left { get; }

        public double Top { get; }

        public double Width { get; }

        public double Height { get; }
    }
}
