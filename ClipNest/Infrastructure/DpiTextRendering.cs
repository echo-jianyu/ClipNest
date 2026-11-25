using System.Windows;
using System.Windows.Media;

namespace ClipNest.Infrastructure;

internal static class DpiTextRendering
{
    internal static void Apply(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        window.Loaded += (_, _) => Update(window);
        window.DpiChanged += (_, _) => Update(window);
    }

    private static void Update(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        TextOptions.SetTextFormattingMode(
            window,
            dpi.PixelsPerInchX > 96.5
                ? TextFormattingMode.Ideal
                : TextFormattingMode.Display);
    }
}
