using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Models;

namespace ClipNest.Tests;

public sealed class ClipItemTests
{
    [Fact]
    public void DisplayText_KeepsFullNormalizedTextForTwoLineTrimming()
    {
        var text = $"  {new string('字', 120)}\r\nsecond line  ";
        var item = ClipItem.Create(ClipContent.FromText(text));

        Assert.Equal(
            $"{new string('字', 120)} second line",
            item.DisplayText);
    }

    [Fact]
    public void ApplyDisplaySettings_UpdatesTypographyAndTwoLineHeight()
    {
        var item = ClipItem.Create(ClipContent.FromText(new string('字', 80)));

        item.ApplyDisplaySettings(fontSize: 16);

        Assert.Equal(16, item.DisplayFontSize);
        Assert.Equal(22.4, item.DisplayLineHeight);
        Assert.Equal(44.8, item.DisplayMaxHeight);
    }

    [Fact]
    public void DisplayText_KeepsShortTextUnchanged()
    {
        var item = ClipItem.Create(ClipContent.FromText("short text"));

        Assert.Equal("short text", item.DisplayText);
    }

    [Fact]
    public void ThumbnailDecode_ConstrainsLongEdgeWithoutUpscaling()
    {
        var tall = ClipItem.DecodeThumbnail(EncodePng(100, 3000));
        var tiny = ClipItem.DecodeThumbnail(EncodePng(20, 20));

        Assert.NotNull(tall);
        Assert.InRange(Math.Max(tall.PixelWidth, tall.PixelHeight), 1, 1280);
        Assert.NotNull(tiny);
        Assert.Equal(20, tiny.PixelWidth);
        Assert.Equal(20, tiny.PixelHeight);
    }

    [Fact]
    public void RestoreImage_RepairsMissingPixelDimensions()
    {
        var item = ClipItem.Restore(
            Guid.NewGuid(),
            ClipContentKind.Image,
            string.Empty,
            rtf: null,
            html: null,
            imagePngBase64: Convert.ToBase64String(EncodePng(8, 6)),
            imageFilePath: null,
            pixelWidth: 0,
            pixelHeight: 0,
            DateTimeOffset.Now,
            pinned: false,
            signature: null,
            sequence: 1);

        Assert.Equal(8, item.PixelWidth);
        Assert.Equal(6, item.PixelHeight);
    }

    private static byte[] EncodePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = Enumerable.Repeat((byte)255, stride * height).ToArray();
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
