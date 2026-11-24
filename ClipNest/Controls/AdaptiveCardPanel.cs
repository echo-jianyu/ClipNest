using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ClipNest.Models;

namespace ClipNest.Controls;

public sealed class AdaptiveCardPanel : Panel
{
    private const double Gap = 7;
    private const double MinimumTextCardHeight = 40;
    private const double MinimumImageCardHeight = 48;
    private const double MaximumImageCardHeight = 220;
    private readonly List<CardPlacement> _placements = [];
    private readonly Dictionary<UIElement, ClipItem> _observedItems = [];

    internal static int GetColumnCount(double width)
    {
        if (width >= 780)
        {
            return 3;
        }

        return width >= 520 ? 2 : 1;
    }

    private static double MeasureCard(
        UIElement child,
        double cardWidth,
        bool isImage)
    {
        var availableHeight = isImage
            ? MaximumImageCardHeight
            : double.PositiveInfinity;
        child.Measure(new Size(cardWidth, availableHeight));

        var minimumHeight = isImage
            ? MinimumImageCardHeight
            : MinimumTextCardHeight;
        var measuredHeight = Math.Max(minimumHeight, child.DesiredSize.Height);
        var finalHeight = isImage
            ? Math.Min(measuredHeight, MaximumImageCardHeight)
            : measuredHeight;
        child.Measure(new Size(cardWidth, finalHeight));
        return finalHeight;
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
            var cardHeight = MeasureCard(
                child,
                columnWidth,
                item?.IsImage == true);
            var column = FindShortestColumn(columnHeights);
            var top = columnHeights[column];
            var left = column * (columnWidth + Gap);

            _placements.Add(new CardPlacement(
                child,
                left,
                top,
                columnWidth,
                cardHeight));
            columnHeights[column] = top + cardHeight + Gap;
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
            or nameof(ClipItem.ImagePreview)
            or nameof(ClipItem.DisplayText)
            or nameof(ClipItem.DisplayFontSize)
            or nameof(ClipItem.PlainText)
            or nameof(ClipItem.Pinned)
            or nameof(ClipItem.IsImage))
        {
            InvalidateMeasure();
        }
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
