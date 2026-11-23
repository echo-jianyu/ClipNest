using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Services;

namespace ClipNest.Tests;

public sealed class ClipboardServiceTests
{
    [Theory]
    [InlineData(10u, 11u, true)]
    [InlineData(10u, 10u, false)]
    public void DidClipboardChange_DetectsPartialWriteSuccess(
        uint sequenceBefore,
        uint sequenceAfter,
        bool expected)
    {
        Assert.Equal(
            expected,
            ClipboardService.DidClipboardChange(sequenceBefore, sequenceAfter));
    }

    [Fact]
    public void IsMatchingOwnCopy_SuppressesMatchingSignatureBeforeExpiry()
    {
        var now = DateTime.UtcNow;

        Assert.True(ClipboardService.IsMatchingOwnCopy(
            "Text:ABC",
            "Text:ABC",
            now.AddSeconds(1),
            now));
    }

    [Fact]
    public void IsMatchingOwnCopy_DoesNotSuppressExternalOrExpiredContent()
    {
        var now = DateTime.UtcNow;

        Assert.False(ClipboardService.IsMatchingOwnCopy(
            "Text:ABC",
            "Text:DEF",
            now.AddSeconds(1),
            now));
        Assert.False(ClipboardService.IsMatchingOwnCopy(
            "Text:ABC",
            "Text:ABC",
            now.AddSeconds(-1),
            now));
    }

    [Fact]
    public void HasVisiblePixels_RejectsFullyTransparentImage()
    {
        var transparentPixels = new byte[2 * 2 * 4];

        Assert.False(ClipboardService.HasVisiblePixels(
            EncodePng(2, 2, transparentPixels)));
    }

    [Fact]
    public void HasVisiblePixels_AcceptsRenderedImage()
    {
        var pixels = new byte[]
        {
            20, 120, 110, 255,
            20, 120, 110, 255,
            20, 120, 110, 255,
            20, 120, 110, 255
        };

        Assert.True(ClipboardService.HasVisiblePixels(
            EncodePng(2, 2, pixels)));
    }

    [Fact]
    public void HasVisiblePixels_AcceptsRgbContentWithZeroAlpha()
    {
        var pixels = new byte[]
        {
            20, 120, 110, 0,
            20, 120, 110, 0,
            20, 120, 110, 0,
            20, 120, 110, 0
        };

        Assert.True(ClipboardService.HasVisiblePixels(
            EncodePng(2, 2, pixels)));
    }

    [Fact]
    public void NormalizeImage_RepairsZeroAlphaClipboardBitmap()
    {
        var pixels = new byte[]
        {
            20, 120, 110, 0,
            20, 120, 110, 0,
            20, 120, 110, 0,
            20, 120, 110, 0
        };
        var source = DecodePng(EncodePng(2, 2, pixels));

        var normalized = ClipboardService.NormalizeImage(source);
        var repaired = DecodePng(normalized.Bytes);
        var converted = new FormatConvertedBitmap(
            repaired,
            PixelFormats.Bgra32,
            destinationPalette: null,
            alphaThreshold: 0);
        var outputPixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(outputPixels, converted.PixelWidth * 4, 0);

        Assert.Equal(255, outputPixels[3]);
        Assert.Equal(255, outputPixels[7]);
    }

    [Fact]
    public void NormalizeImage_PreservesAlphaRepairWhenDownscaling()
    {
        const int width = 2600;
        const int height = 100;
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 20;
            pixels[index + 1] = 120;
            pixels[index + 2] = 110;
        }

        var source = DecodePng(EncodePng(width, height, pixels));
        var normalized = ClipboardService.NormalizeImage(source);
        var repaired = DecodePng(normalized.Bytes);
        var converted = new FormatConvertedBitmap(
            repaired,
            PixelFormats.Bgra32,
            destinationPalette: null,
            alphaThreshold: 0);
        var outputPixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(outputPixels, converted.PixelWidth * 4, 0);

        Assert.Equal(2560, normalized.Width);
        Assert.InRange(normalized.Height, 96, 100);
        Assert.Equal(255, outputPixels[3]);
        Assert.Equal(255, outputPixels[outputPixels.Length - 1]);
    }

    [Fact]
    public void IsUsableClipboardImage_RejectsPlaceholderDimensions()
    {
        var content = ClipNest.Models.ClipContent.FromImage(
            EncodePng(1, 1, [20, 120, 110, 255]),
            1,
            1);

        Assert.False(ClipboardService.IsUsableClipboardImage(content));
    }

    private static byte[] EncodePng(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
