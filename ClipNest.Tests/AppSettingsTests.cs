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

    [Fact]
    public void Normalize_ResetsInvalidHotKeyToDefault()
    {
        var settings = new AppSettings
        {
            HotKeyModifiers = 0x80000000,
            HotKeyKey = 0
        };

        settings.Normalize();

        Assert.Equal(HotKeyDefinition.Default.Modifiers, settings.HotKeyModifiers);
        Assert.Equal(HotKeyDefinition.Default.VirtualKey, settings.HotKeyKey);
    }

    [Fact]
    public void Normalize_ClampsCardDisplaySettings()
    {
        var settings = new AppSettings
        {
            CardTextLimit = 500,
            CardFontSize = 4
        };

        settings.Normalize();

        Assert.Equal(AppSettings.MaximumCardTextLimit, settings.CardTextLimit);
        Assert.Equal(AppSettings.MinimumCardFontSize, settings.CardFontSize);
    }

    [Theory]
    [InlineData(0u, 0x73u, true)]
    [InlineData(HotKeyDefinition.ControlModifier, 0x56u, true)]
    [InlineData(HotKeyDefinition.ControlModifier, 0x11u, false)]
    public void HotKeyDefinition_ValidatesVirtualKeys(
        uint modifiers,
        uint virtualKey,
        bool expected)
    {
        Assert.Equal(expected, HotKeyDefinition.IsValid(modifiers, virtualKey));
    }
}
