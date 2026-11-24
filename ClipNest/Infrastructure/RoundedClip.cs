using System.Windows;
using System.Windows.Media;

namespace ClipNest.Infrastructure;

public static class RoundedClip
{
    public static readonly DependencyProperty BottomRadiusProperty =
        DependencyProperty.RegisterAttached(
            "BottomRadius",
            typeof(double),
            typeof(RoundedClip),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnBottomRadiusChanged));

    public static double GetBottomRadius(DependencyObject element)
    {
        return (double)element.GetValue(BottomRadiusProperty);
    }

    public static void SetBottomRadius(DependencyObject element, double value)
    {
        element.SetValue(BottomRadiusProperty, value);
    }

    private static void OnBottomRadiusChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not FrameworkElement element)
        {
            return;
        }

        element.SizeChanged -= Element_SizeChanged;
        if (eventArgs.NewValue is double radius && radius > 0)
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
        var radius = GetBottomRadius(element);
        if (radius <= 0
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            element.Clip = null;
            return;
        }

        radius = Math.Min(
            radius,
            Math.Min(element.ActualWidth / 2, element.ActualHeight / 2));
        var width = element.ActualWidth;
        var height = element.ActualHeight;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
            context.LineTo(new Point(width, 0), isStroked: true, isSmoothJoin: false);
            context.LineTo(
                new Point(width, height - radius),
                isStroked: true,
                isSmoothJoin: false);
            context.ArcTo(
                new Point(width - radius, height),
                new Size(radius, radius),
                0,
                isLargeArc: false,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
            context.LineTo(
                new Point(radius, height),
                isStroked: true,
                isSmoothJoin: false);
            context.ArcTo(
                new Point(0, height - radius),
                new Size(radius, radius),
                0,
                isLargeArc: false,
                SweepDirection.Clockwise,
                isStroked: true,
                isSmoothJoin: false);
            context.LineTo(new Point(0, 0), isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        element.Clip = geometry;
    }
}
