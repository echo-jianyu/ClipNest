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
        context.PushTransform(new ScaleTransform(scale, scale));

        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(23, 109, 105)),
            new Pen(new SolidColorBrush(Color.FromRgb(14, 85, 82)), 1.5),
            new Rect(0.75, 0.75, 254.5, 254.5),
            57.25,
            57.25);

        context.PushTransform(new RotateTransform(-8, 96, 118));
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(229, 242, 237)),
            null,
            new Rect(48, 48, 112, 136),
            20,
            20);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(154, 188, 176)),
            null,
            new Rect(64, 77, 80, 10),
            5,
            5);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(182, 206, 197)),
            null,
            new Rect(64, 101, 64, 10),
            5,
            5);
        context.Pop();

        context.PushTransform(new RotateTransform(4, 145, 132));
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(255, 254, 251)),
            new Pen(new SolidColorBrush(Color.FromRgb(217, 230, 223)), 1.5),
            new Rect(94.75, 47.75, 108.5, 140.5),
            18.25,
            18.25);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(23, 109, 105)),
            null,
            new Rect(107, 82, 73, 9),
            4.5,
            4.5);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(185, 201, 194)),
            null,
            new Rect(107, 103, 58, 8),
            4,
            4);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(210, 221, 216)),
            null,
            new Rect(107, 121, 68, 8),
            4,
            4);

        var clip = new StreamGeometry();
        using (var clipContext = clip.Open())
        {
            clipContext.BeginFigure(new Point(125, 47), true, true);
            clipContext.LineTo(new Point(125, 33), true, true);
            clipContext.BezierTo(new Point(125, 27), new Point(130, 23), new Point(135, 23), true, true);
            clipContext.LineTo(new Point(157, 23), true, true);
            clipContext.BezierTo(new Point(162, 23), new Point(167, 27), new Point(167, 33), true, true);
            clipContext.LineTo(new Point(167, 47), true, true);
            clipContext.LineTo(new Point(125, 47), true, true);
        }

        clip.Freeze();
        context.DrawGeometry(new SolidColorBrush(Color.FromRgb(223, 96, 73)), null, clip);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(249, 161, 142)),
            null,
            new Rect(131, 30, 30, 8),
            4,
            4);
        context.Pop();

        context.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(223, 96, 73)),
            null,
            new Point(190, 178),
            24,
            24);

        var check = new StreamGeometry();
        using (var checkContext = check.Open())
        {
            checkContext.BeginFigure(new Point(181, 178), false, false);
            checkContext.LineTo(new Point(187, 184), true, true);
            checkContext.LineTo(new Point(199, 171), true, true);
        }

        check.Freeze();
        context.DrawGeometry(
            null,
            new Pen(new SolidColorBrush(Color.FromRgb(255, 254, 251)), 6)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            },
            check);
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
