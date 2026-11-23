using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class ClipContentTests
{
    [Fact]
    public void TextSignature_IsStableForSamePlainText()
    {
        var first = ClipContent.FromText("same", "rtf-a", "html-a");
        var second = ClipContent.FromText("same", "rtf-b", "html-b");

        Assert.Equal(first.Signature, second.Signature);
    }

    [Fact]
    public void TextAndImageSignatures_AreDistinct()
    {
        var text = ClipContent.FromText("content");
        var image = ClipContent.FromImage([1, 2, 3], 1, 1);

        Assert.NotEqual(text.Signature, image.Signature);
    }

    [Fact]
    public void FromText_RejectsWhitespaceOnlyContent()
    {
        Assert.Throws<ArgumentException>(() => ClipContent.FromText("   "));
    }
}
