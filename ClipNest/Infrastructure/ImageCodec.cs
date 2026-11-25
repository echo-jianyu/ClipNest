using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipNest.Infrastructure;

internal static class ImageCodec
{
    internal static BitmapImage Decode(byte[] bytes, int? maxLongEdge = null)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        using var stream = new MemoryStream(bytes, writable: false);
        int? decodeWidth = null;
        int? decodeHeight = null;

        if (maxLongEdge is > 0)
        {
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var longestSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
            if (longestSide > maxLongEdge.Value)
            {
                if (frame.PixelWidth >= frame.PixelHeight)
                {
                    decodeWidth = maxLongEdge.Value;
                }
                else
                {
                    decodeHeight = maxLongEdge.Value;
                }
            }

            stream.Position = 0;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth.HasValue)
        {
            bitmap.DecodePixelWidth = decodeWidth.Value;
        }
        else if (decodeHeight.HasValue)
        {
            bitmap.DecodePixelHeight = decodeHeight.Value;
        }

        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static BitmapImage? TryDecode(byte[]? bytes, int? maxLongEdge = null)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            return Decode(bytes, maxLongEdge);
        }
        catch (Exception exception) when (
            exception is FileFormatException
            or FormatException
            or NotSupportedException
            or ArgumentException)
        {
            return null;
        }
    }

    internal static (int Width, int Height)? TryReadDimensions(Stream stream)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        try
        {
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            return frame.PixelWidth > 0 && frame.PixelHeight > 0
                ? (frame.PixelWidth, frame.PixelHeight)
                : null;
        }
        catch (Exception exception) when (
            exception is FileFormatException
            or FormatException
            or IOException
            or NotSupportedException
            or ArgumentException)
        {
            return null;
        }
    }

    internal static BitmapSource Rotate(BitmapSource source, int angle)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var rotated = new TransformedBitmap(source, new RotateTransform(angle));
        rotated.Freeze();
        return rotated;
    }

    internal static byte[] EncodePng(BitmapSource source)
    {
        if (source is null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
