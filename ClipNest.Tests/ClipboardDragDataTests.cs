using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Tests;

public sealed class ClipboardDragDataTests
{
    [Fact]
    public void BuildDragDataObject_TextIncludesAllEditingFormats()
    {
        const string text = "ClipNest drag";
        const string rtf = @"{\rtf1\ansi ClipNest drag}";
        const string html = "<html><body>ClipNest drag</body></html>";
        var item = ClipItem.Create(ClipContent.FromText(text, rtf, html));

        var dataObject = ClipboardService.BuildDragDataObject(item);

        Assert.Equal(text, dataObject.GetData(DataFormats.UnicodeText));
        Assert.Equal(text, dataObject.GetData(DataFormats.Text));
        Assert.Equal(rtf, dataObject.GetData(DataFormats.Rtf));
        Assert.Equal(html, dataObject.GetData(DataFormats.Html));
        Assert.False(dataObject.GetDataPresent(DataFormats.FileDrop));
    }

    [Fact]
    public void BuildDragDataObject_ImageIncludesBitmapPngAndFileDrop()
    {
        var pngBytes = EncodePng();
        var item = ClipItem.Create(ClipContent.FromImage(pngBytes, 2, 2));

        var dataObject = ClipboardService.BuildDragDataObject(item);
        var files = Assert.IsType<string[]>(
            dataObject.GetData(DataFormats.FileDrop));

        Assert.True(dataObject.GetDataPresent(DataFormats.Bitmap));
        Assert.True(dataObject.GetDataPresent("PNG"));
        var imagePath = Assert.Single(files);
        Assert.Equal(".png", Path.GetExtension(imagePath), ignoreCase: true);

        try
        {
            Assert.True(File.Exists(imagePath));
            Assert.Equal(pngBytes, File.ReadAllBytes(imagePath));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void BuildDragDataObject_UsesTemporaryFileInsteadOfHistoryImage()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ClipNest.DragSource.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "history.png");
        var pngBytes = EncodePng();
        File.WriteAllBytes(sourcePath, pngBytes);

        try
        {
            var item = ClipItem.Create(ClipContent.FromImage(pngBytes, 2, 2));
            item.AttachImageFile(sourcePath, notify: false);

            var dataObject = ClipboardService.BuildDragDataObject(item);
            var files = Assert.IsType<string[]>(
                dataObject.GetData(DataFormats.FileDrop));
            var dragPath = Assert.Single(files);

            Assert.NotEqual(sourcePath, dragPath);
            Assert.Equal(pngBytes, File.ReadAllBytes(dragPath));
            Assert.True(File.Exists(sourcePath));
            File.Delete(dragPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] EncodePng()
    {
        var pixels = new byte[]
        {
            20, 120, 110, 255,
            20, 120, 110, 255,
            20, 120, 110, 255,
            20, 120, 110, 255
        };
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            2 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
