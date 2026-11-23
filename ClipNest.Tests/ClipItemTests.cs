using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class ClipItemTests
{
    [Fact]
    public void DefaultDisplayTextLimit_IsFortyCharacters()
    {
        Assert.Equal(40, ClipItem.DefaultDisplayTextLimit);
    }

    [Fact]
    public void DisplayText_TruncatesAfterDisplayLimit()
    {
        var item = ClipItem.Create(ClipContent.FromText(
            new string('字', ClipItem.DefaultDisplayTextLimit + 20)));

        Assert.Equal(ClipItem.DefaultDisplayTextLimit + 1, item.DisplayText.Length);
        Assert.Equal(
            new string('字', ClipItem.DefaultDisplayTextLimit) + "…",
            item.DisplayText);
    }

    [Fact]
    public void ApplyDisplaySettings_UpdatesPreviewAndTypography()
    {
        var item = ClipItem.Create(ClipContent.FromText(new string('字', 80)));

        item.ApplyDisplaySettings(textLimit: 24, fontSize: 16);

        Assert.Equal(25, item.DisplayText.Length);
        Assert.Equal(16, item.DisplayFontSize);
        Assert.Equal(22.4, item.DisplayLineHeight);
    }

    [Fact]
    public void DisplayText_KeepsShortTextUnchanged()
    {
        var item = ClipItem.Create(ClipContent.FromText("short text"));

        Assert.Equal("short text", item.DisplayText);
    }

}
