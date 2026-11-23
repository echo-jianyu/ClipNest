using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(100, 450)]
    [InlineData(450, 450)]
    [InlineData(1800, 900)]
    public void Normalize_ClampsWidthAndMaintainsAspectRatio(double requestedWidth, double expectedWidth)
    {
        var settings = new AppSettings { Width = requestedWidth, Height = 900 };

        settings.Normalize();

        Assert.Equal(expectedWidth, settings.Width);
        Assert.Equal(expectedWidth / AppSettings.AspectRatio, settings.Height, precision: 6);
    }
}
