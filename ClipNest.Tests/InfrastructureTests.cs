using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Infrastructure;

namespace ClipNest.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void ImageHash_UsesSignatureAndProducesLowercaseFileName()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var signature =
            "Image:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ContentHashing.ResolveImageHashHex(signature, bytes));
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.png",
            ContentHashing.CreateImageFileName(signature, bytes));
    }

    [Fact]
    public void ImageCodec_ReadsDimensionsAndEncodesPng()
    {
        var bitmap = BitmapSource.Create(
            3,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            new byte[3 * 2 * 4],
            3 * 4);

        var bytes = ImageCodec.EncodePng(bitmap);
        using var stream = new MemoryStream(bytes, writable: false);
        var dimensions = ImageCodec.TryReadDimensions(stream);
        var decoded = ImageCodec.TryDecode(bytes);

        Assert.Equal((3, 2), dimensions);
        Assert.NotNull(decoded);
        Assert.Equal(3, decoded.PixelWidth);
        Assert.Equal(2, decoded.PixelHeight);
    }

    [Fact]
    public void ImageCodec_ReturnsNullForInvalidImageData()
    {
        Assert.Null(ImageCodec.TryDecode([1, 2, 3]));
    }
}
