using System.Windows.Input;
using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void NewSettings_DisableAutoStartByDefault()
    {
        Assert.False(new AppSettings().AutoStartOnLogin);
    }

    [Theory]
    [InlineData(100, 100, 320, 240)]
    [InlineData(400, 400, 400, 400)]
    [InlineData(450, 700, 450, 700)]
    [InlineData(500, 100, 500, 240)]
    [InlineData(1800, 1200, 900, 900)]
    public void Normalize_ClampsWidthAndHeightIndependently(
        double requestedWidth,
        double requestedHeight,
        double expectedWidth,
        double expectedHeight)
    {
        var settings = new AppSettings
        {
            Width = requestedWidth,
            Height = requestedHeight
        };

        settings.Normalize();

        Assert.Equal(expectedWidth, settings.Width);
        Assert.Equal(expectedHeight, settings.Height);
    }

    [Fact]
    public void Normalize_ResetsLegacyWindowLayoutOnce()
    {
        var settings = new AppSettings
        {
            Width = 580,
            Height = 386.66666666666669,
            LayoutVersion = 0
        };

        settings.Normalize();

        Assert.Equal(400, settings.Width);
        Assert.Equal(400, settings.Height);
        Assert.Equal(1, settings.LayoutVersion);

        settings.Width = 580;
        settings.Height = 386.66666666666669;
        settings.Normalize();

        Assert.Equal(580, settings.Width);
        Assert.Equal(386.66666666666669, settings.Height, precision: 6);
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
    [InlineData(0u, 0x41u, false)]
    [InlineData(HotKeyDefinition.ShiftModifier, 0x41u, false)]
    [InlineData(HotKeyDefinition.ShiftModifier, 0x70u, false)]
    [InlineData(
        HotKeyDefinition.ControlModifier | HotKeyDefinition.ShiftModifier,
        0x41u,
        true)]
    public void HotKeyDefinition_ValidatesVirtualKeys(
        uint modifiers,
        uint virtualKey,
        bool expected)
    {
        Assert.Equal(expected, HotKeyDefinition.IsValid(modifiers, virtualKey));
    }

    [Theory]
    [InlineData(0x73u)]
    [InlineData(0x77u)]
    public void HotKeyDefinition_AcceptsBareFunctionKeys(uint virtualKey)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)virtualKey);

        var hotKey = HotKeyDefinition.FromKeyboard(key, ModifierKeys.None);

        Assert.NotNull(hotKey);
        Assert.Equal(virtualKey, hotKey.VirtualKey);
        Assert.Equal(0u, hotKey.Modifiers);
    }

    [Fact]
    public void HotKeyDefinition_RejectsBareCharacterKeys()
    {
        var hotKey = HotKeyDefinition.FromKeyboard(
            System.Windows.Input.Key.A,
            ModifierKeys.None);

        Assert.Null(hotKey);
    }
}
