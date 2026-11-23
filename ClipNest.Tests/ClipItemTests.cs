using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class ClipItemTests
{
    [Fact]
    public void DisplayText_TruncatesAfterFortyCharacters()
    {
        var item = ClipItem.Create(ClipContent.FromText(new string('字', 60)));

        Assert.Equal(41, item.DisplayText.Length);
        Assert.Equal(new string('字', 40) + "…", item.DisplayText);
    }

    [Fact]
    public void DisplayText_KeepsShortTextUnchanged()
    {
        var item = ClipItem.Create(ClipContent.FromText("short text"));

        Assert.Equal("short text", item.DisplayText);
    }
}
