using System.Windows;
using System.Windows.Media;

namespace ClipNest.Infrastructure;

public static class RoundedClip
{
    public static readonly DependencyProperty TopRadiusProperty =
        DependencyProperty.RegisterAttached(
            "TopRadius",
            typeof(double),
            typeof(RoundedClip),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnRadiusChanged));

    public static readonly DependencyProperty BottomRadiusProperty =
        DependencyProperty.RegisterAttached(
            "BottomRadius",
            typeof(double),
            typeof(RoundedClip),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnRadiusChanged));

    public static double GetTopRadius(DependencyObject element)
    {
        return (double)element.GetValue(TopRadiusProperty);
    }

    public static void SetTopRadius(DependencyObject element, double value)
    {
        element.SetValue(TopRadiusProperty, value);
    }

    public static double GetBottomRadius(DependencyObject element)
    {
        return (double)element.GetValue(BottomRadiusProperty);
    }

    public static void SetBottomRadius(DependencyObject element, double value)
    {
        element.SetValue(BottomRadiusProperty, value);
    }

    private static void OnRadiusChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= Element_SizeChanged;
        if (GetTopRadius(element) > 0 || GetBottomRadius(element) > 0)
        {
            element.SizeChanged += Element_SizeChanged;
        }

        UpdateClip(element);
    }

    private static void Element_SizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        UpdateClip((FrameworkElement)sender);
    }

    private static void UpdateClip(FrameworkElement element)
    {
        var topRadius = GetTopRadius(element);
        var bottomRadius = GetBottomRadius(element);
        if ((topRadius <= 0
                && bottomRadius <= 0)
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            element.Clip = null;
            return;
        }

        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var maximumRadius = Math.Min(width / 2, height / 2);
        topRadius = Math.Min(Math.Max(topRadius, 0), maximumRadius);
        bottomRadius = Math.Min(Math.Max(bottomRadius, 0), maximumRadius);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(
                new Point(topRadius, 0),
                isFilled: true,
                isClosed: true);
            context.LineTo(
                new Point(width - topRadius, 0),
                isStroked: true,
                isSmoothJoin: false);
            if (topRadius > 0)
            {
                context.ArcTo(
                    new Point(width, topRadius),
                    new Size(topRadius, topRadius),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }

            context.LineTo(
                new Point(width, height - bottomRadius),
                isStroked: true,
                isSmoothJoin: false);
            if (bottomRadius > 0)
            {
                context.ArcTo(
                    new Point(width - bottomRadius, height),
                    new Size(bottomRadius, bottomRadius),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }

            context.LineTo(
                new Point(bottomRadius, height),
                isStroked: true,
                isSmoothJoin: false);
            if (bottomRadius > 0)
            {
                context.ArcTo(
                    new Point(0, height - bottomRadius),
                    new Size(bottomRadius, bottomRadius),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }

            context.LineTo(new Point(0, topRadius), isStroked: true, isSmoothJoin: false);
            if (topRadius > 0)
            {
                context.ArcTo(
                    new Point(topRadius, 0),
                    new Size(topRadius, topRadius),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: true,
                    isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        element.Clip = geometry;
    }
}
