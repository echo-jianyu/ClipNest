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
    private const double MaximumImageCardHeight = 150;
    private readonly List<CardPlacement> _placements = [];
    private readonly Dictionary<UIElement, ClipItem> _observedItems = [];

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
        if (Math.Abs(finalHeight - child.DesiredSize.Height) > 0.01)
        {
            child.Measure(new Size(cardWidth, finalHeight));
        }

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
            ? 400
            : availableSize.Width;
        var columnWidth = Math.Max(1, width);
        var top = 0d;

        foreach (UIElement child in InternalChildren)
        {
            var item = (child as FrameworkElement)?.DataContext as ClipItem;
            var cardHeight = MeasureCard(
                child,
                columnWidth,
                item?.IsImage == true);

            _placements.Add(new CardPlacement(
                child,
                0,
                top,
                columnWidth,
                cardHeight));
            top += cardHeight + Gap;
        }

        var height = Math.Max(0, top - Gap);
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

    private readonly struct CardPlacement
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
