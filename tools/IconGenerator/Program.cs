using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var outputPath = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ClipNest", "Assets", "ClipNest.ico"));

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(size => (Size: size, Data: EncodePng(Render(size)))).ToArray();

using var stream = File.Create(outputPath);
using var writer = new BinaryWriter(stream);
writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)frames.Length);

var dataOffset = 6 + (16 * frames.Length);
foreach (var frame in frames)
{
    writer.Write(frame.Size == 256 ? (byte)0 : (byte)frame.Size);
    writer.Write(frame.Size == 256 ? (byte)0 : (byte)frame.Size);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write((uint)frame.Data.Length);
    writer.Write((uint)dataOffset);
    dataOffset += frame.Data.Length;
}

foreach (var frame in frames)
{
    writer.Write(frame.Data);
}

Console.WriteLine($"Wrote {outputPath}");

static RenderTargetBitmap Render(int size)
{
    var visual = new DrawingVisual();
    using (var context = visual.RenderOpen())
    {
        var scale = size / 256d;
        var clipboardStrokeWidth = size <= 20 ? 18d : size <= 32 ? 15d : 12d;
        var clipStrokeWidth = size <= 20 ? 14d : size <= 32 ? 12d : 10d;
        var contentLineHeight = size <= 20 ? 12d : size <= 32 ? 16d : 12d;
        var contentLineGap = size <= 20 ? 16d : size <= 32 ? 14d : 16d;
        var firstLineWidth = size <= 20 ? 64d : size <= 32 ? 62d : 53d;
        var secondLineWidth = size <= 20 ? 110d : size <= 32 ? 114d : 108d;
        var thirdLineWidth = size <= 20 ? 82d : size <= 32 ? 80d : 74d;
        context.PushTransform(new ScaleTransform(scale, scale));
        context.PushTransform(new ScaleTransform(1.12, 1.12, 128, 127));

        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(249, 252, 251)),
            new Pen(
                new SolidColorBrush(Color.FromRgb(47, 110, 103)),
                clipboardStrokeWidth),
            new Rect(50, 34, 156, 199),
            34,
            34);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(238, 140, 115)),
            new Pen(
                new SolidColorBrush(Color.FromRgb(47, 110, 103)),
                clipStrokeWidth),
            new Rect(96, 21, 64, 32),
            12,
            12);

        var contentGroupHeight = (contentLineHeight * 3) + (contentLineGap * 2);
        var contentTop = 134 - (contentGroupHeight / 2);
        var contentLeft = 128 - (secondLineWidth / 2);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(110, 166, 156)),
            null,
            new Rect(
                contentLeft,
                contentTop,
                thirdLineWidth,
                contentLineHeight),
            contentLineHeight / 2,
            contentLineHeight / 2);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(232, 154, 134)),
            null,
            new Rect(
                contentLeft,
                contentTop + contentLineHeight + contentLineGap,
                firstLineWidth,
                contentLineHeight),
            contentLineHeight / 2,
            contentLineHeight / 2);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(174, 204, 197)),
            null,
            new Rect(
                contentLeft,
                contentTop + ((contentLineHeight + contentLineGap) * 2),
                secondLineWidth,
                contentLineHeight),
            contentLineHeight / 2,
            contentLineHeight / 2);
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

static byte[] EncodePng(BitmapSource bitmap)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));

    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}
