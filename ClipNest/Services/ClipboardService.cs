using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipNest.Infrastructure;
using ClipNest.Models;
using FormsClipboard = System.Windows.Forms.Clipboard;
using WpfClipboard = System.Windows.Clipboard;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.DataObject;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace ClipNest.Services;

public sealed class ClipboardService : IClipboardService
{
    private const int MaxImageDimension = 2560;
    private const string InternalCopyFormat = "ClipNest.InternalCopy";

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _captureTimer;
    private uint? _ignoredSequence;
    private string? _ownCopySignature;
    private DateTime _ownCopyExpiryUtc;
    private bool _disposed;

    public event EventHandler<ClipContent>? ContentChanged;

    public void Attach(Window window)
    {
        if (window is null)
        {
            throw new ArgumentNullException(nameof(window));
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClipboardService));
        }

        if (_windowHandle != IntPtr.Zero)
        {
            throw new InvalidOperationException("ClipboardService is already attached.");
        }

        _windowHandle = new WindowInteropHelper(window).Handle;
        _dispatcher = window.Dispatcher;
        _source = HwndSource.FromHwnd(_windowHandle);

        if (_source is null)
        {
            _windowHandle = IntPtr.Zero;
            throw new InvalidOperationException("The window message source is unavailable.");
        }

        _source.AddHook(WindowMessageHook);

        if (!NativeMethods.AddClipboardFormatListener(_windowHandle))
        {
            _source.RemoveHook(WindowMessageHook);
            _source = null;
            _windowHandle = IntPtr.Zero;
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to listen for clipboard changes.");
        }

        ScheduleClipboardCapture();
    }

    public void Detach()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        _captureTimer?.Stop();
        _captureTimer = null;
        NativeMethods.RemoveClipboardFormatListener(_windowHandle);
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        _dispatcher = null;
        _windowHandle = IntPtr.Zero;
    }

    public bool Copy(ClipItem item, bool includeFormatting)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClipboardService));
        }

        var sequenceBefore = NativeMethods.GetClipboardSequenceNumber();
        _ownCopySignature = item.Signature;
        _ownCopyExpiryUtc = DateTime.UtcNow.AddSeconds(2);

        var succeeded = false;
        MemoryStream? pngStream = null;
        try
        {
            var dataObject = BuildDataObject(item, includeFormatting, out pngStream);
            WpfClipboard.SetDataObject(dataObject, true);
            succeeded = true;
        }
        catch (Exception exception) when (
            exception is ExternalException
            or InvalidOperationException)
        {
        }
        finally
        {
            pngStream?.Dispose();

            var sequenceAfter = NativeMethods.GetClipboardSequenceNumber();
            if (DidClipboardChange(sequenceBefore, sequenceAfter))
            {
                // Some clipboard providers report a COM error after already
                // publishing the content. A changed sequence means the copy succeeded.
                _ignoredSequence = sequenceAfter;
                succeeded = true;
            }
            else if (!succeeded && ClipboardContains(item))
            {
                // A few delayed-rendering providers omit a sequence update even
                // though the requested content is already available.
                _ignoredSequence = sequenceAfter;
                succeeded = true;
            }
            else if (!succeeded)
            {
                _ownCopySignature = null;
                _ownCopyExpiryUtc = DateTime.MinValue;
            }
        }

        return succeeded;
    }

    private static WpfDataObject BuildDataObject(
        ClipItem item,
        bool includeFormatting,
        out MemoryStream? pngStream)
    {
        var dataObject = new WpfDataObject();
        pngStream = null;
        dataObject.SetData(InternalCopyFormat, "ClipNest");

        if (item.Kind == ClipContentKind.Image)
        {
            var bytes = Convert.FromBase64String(item.ImagePngBase64
                ?? throw new InvalidOperationException("Image data is missing."));
            var image = DecodeImage(bytes);
            dataObject.SetData(WpfDataFormats.Bitmap, image);

            pngStream = new MemoryStream(bytes, writable: false);
            dataObject.SetData("PNG", pngStream);
            return dataObject;
        }

        dataObject.SetData(WpfDataFormats.UnicodeText, item.PlainText);
        dataObject.SetData(WpfDataFormats.Text, item.PlainText);

        if (!includeFormatting)
        {
            return dataObject;
        }

        if (!string.IsNullOrWhiteSpace(item.Rtf))
        {
            dataObject.SetData(WpfDataFormats.Rtf, item.Rtf);
        }

        if (!string.IsNullOrWhiteSpace(item.Html))
        {
            dataObject.SetData(WpfDataFormats.Html, item.Html);
        }

        return dataObject;
    }

    private static BitmapImage DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != NativeMethods.WmClipboardUpdate)
        {
            return IntPtr.Zero;
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (_ignoredSequence == sequence)
        {
            _ignoredSequence = null;
            handled = true;
            return IntPtr.Zero;
        }

        if (_dispatcher is not null && !_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(ScheduleClipboardCapture));
        }
        else
        {
            ScheduleClipboardCapture();
        }

        handled = true;
        return IntPtr.Zero;
    }

    private void CaptureClipboard()
    {
        if (_disposed || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var content = ReadClipboard();
                if (content is null)
                {
                    return;
                }

                if (content.Kind == ClipContentKind.Image
                    && !IsUsableClipboardImage(content))
                {
                    if (attempt < 5)
                    {
                        Thread.Sleep(40);
                        continue;
                    }

                    return;
                }

                if (!IsOwnCopyEcho(content))
                {
                    ContentChanged?.Invoke(this, content);
                }

                return;
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    private void ScheduleClipboardCapture()
    {
        if (_disposed || _dispatcher is null || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(ScheduleClipboardCapture));
            return;
        }

        if (_captureTimer is null)
        {
            _captureTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };
            _captureTimer.Tick += (_, _) =>
            {
                _captureTimer.Stop();
                CaptureClipboard();
            };
        }

        _captureTimer.Stop();
        _captureTimer.Start();
    }

    private bool IsOwnCopyEcho(ClipContent content)
    {
        if (!IsMatchingOwnCopy(
            _ownCopySignature,
            content.Signature,
            _ownCopyExpiryUtc,
            DateTime.UtcNow))
        {
            return false;
        }

        _ownCopySignature = null;
        _ownCopyExpiryUtc = DateTime.MinValue;
        return true;
    }

    internal static bool DidClipboardChange(uint sequenceBefore, uint sequenceAfter)
    {
        return sequenceBefore != sequenceAfter;
    }

    internal static bool IsMatchingOwnCopy(
        string? expectedSignature,
        string? actualSignature,
        DateTime expiryUtc,
        DateTime nowUtc)
    {
        return expectedSignature is not null
            && string.Equals(expectedSignature, actualSignature, StringComparison.Ordinal)
            && nowUtc <= expiryUtc;
    }

    private static bool ClipboardContains(ClipItem item)
    {
        try
        {
            var content = ReadClipboard(ignoreInternalMarker: true);
            return content is not null
                && string.Equals(content.Signature, item.Signature, StringComparison.Ordinal);
        }
        catch (ExternalException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static ClipContent? ReadClipboard(bool ignoreInternalMarker = false)
    {
        var dataObject = WpfClipboard.GetDataObject();
        if (dataObject is null
            || (!ignoreInternalMarker && dataObject.GetDataPresent(InternalCopyFormat)))
        {
            return null;
        }

        var image = TryReadImage(dataObject);
        if (image is not null)
        {
            var content = NormalizeImageContent(image);
            if (IsUsableClipboardImage(content))
            {
                return content;
            }
        }

        var formsImage = TryReadFormsImage();
        if (formsImage is not null)
        {
            var content = NormalizeImageContent(formsImage);
            if (IsUsableClipboardImage(content))
            {
                return content;
            }
        }

        var text = WpfClipboard.GetText(WpfTextDataFormat.UnicodeText);
        if (string.IsNullOrWhiteSpace(text))
        {
            text = WpfClipboard.GetText(WpfTextDataFormat.Text);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var rtf = dataObject.GetDataPresent(WpfDataFormats.Rtf)
            ? dataObject.GetData(WpfDataFormats.Rtf) as string
            : null;
        var html = dataObject.GetDataPresent(WpfDataFormats.Html)
            ? dataObject.GetData(WpfDataFormats.Html) as string
            : null;

        return ClipContent.FromText(text, rtf, html);
    }

    private static ClipContent NormalizeImageContent(BitmapSource image)
    {
        var normalized = NormalizeImage(image);
        return ClipContent.FromImage(normalized.Bytes, normalized.Width, normalized.Height);
    }

    private static BitmapSource? TryReadImage(System.Windows.IDataObject dataObject)
    {
        try
        {
            if (dataObject.GetDataPresent("PNG", true))
            {
                var pngBytes = TryReadBytes(dataObject.GetData("PNG", true));
                var pngImage = TryDecodeImage(pngBytes);
                if (pngImage is not null)
                {
                    return pngImage;
                }
            }
        }
        catch (ExternalException)
        {
        }

        try
        {
            if (dataObject.GetDataPresent(WpfDataFormats.Bitmap, true)
                || dataObject.GetDataPresent(WpfDataFormats.Dib, true))
            {
                return WpfClipboard.GetImage();
            }
        }
        catch (ExternalException)
        {
        }

        return null;
    }

    private static BitmapImage? TryReadFormsImage()
    {
        try
        {
            using var image = FormsClipboard.GetImage();
            if (image is null)
            {
                return null;
            }

            using var stream = new MemoryStream();
            image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return DecodeImage(stream.ToArray());
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static byte[]? TryReadBytes(object? clipboardData)
    {
        switch (clipboardData)
        {
            case byte[] bytes:
                return bytes;
            case MemoryStream memoryStream:
                if (memoryStream.CanSeek)
                {
                    memoryStream.Position = 0;
                }

                return memoryStream.ToArray();
            case Stream stream:
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }

                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            default:
                return null;
        }
    }

    private static BitmapImage? TryDecodeImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            return DecodeImage(bytes);
        }
        catch (FileFormatException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static bool HasVisiblePixels(byte[]? pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return false;
        }

        try
        {
            var bitmap = (BitmapImage)DecodeImage(pngBytes);
            BitmapSource source = bitmap;
            if (source.PixelWidth > 64 || source.PixelHeight > 64)
            {
                var scale = Math.Min(64d / source.PixelWidth, 64d / source.PixelHeight);
                var thumbnail = new TransformedBitmap(
                    source,
                    new System.Windows.Media.ScaleTransform(scale, scale));
                thumbnail.Freeze();
                source = thumbnail;
            }

            var converted = new FormatConvertedBitmap(
                source,
                System.Windows.Media.PixelFormats.Bgra32,
                destinationPalette: null,
                alphaThreshold: 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);

            for (var index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] != 0
                    || pixels[index] != 0
                    || pixels[index + 1] != 0
                    || pixels[index + 2] != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (FileFormatException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsUsableClipboardImage(ClipContent content)
    {
        return content.Kind != ClipContentKind.Image
            || (content.PixelWidth > 1
                && content.PixelHeight > 1
                && HasVisiblePixels(content.ImagePng));
    }

    internal static (byte[] Bytes, int Width, int Height) NormalizeImage(BitmapSource source)
    {
        BitmapSource output = RepairClipboardAlpha(source);
        var longestSide = Math.Max(source.PixelWidth, source.PixelHeight);

        if (longestSide > MaxImageDimension)
        {
            var scale = (double)MaxImageDimension / longestSide;
            var transformed = new TransformedBitmap(
                source,
                new System.Windows.Media.ScaleTransform(scale, scale));
            transformed.Freeze();
            output = transformed;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return (stream.ToArray(), output.PixelWidth, output.PixelHeight);
    }

    private static BitmapSource RepairClipboardAlpha(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(
            source,
            System.Windows.Media.PixelFormats.Bgra32,
            destinationPalette: null,
            alphaThreshold: 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var hasVisibleColor = false;
        var hasVisibleAlpha = false;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0)
            {
                hasVisibleColor = true;
            }

            if (pixels[index + 3] != 0)
            {
                hasVisibleAlpha = true;
            }
        }

        if (!hasVisibleColor || hasVisibleAlpha)
        {
            return converted;
        }

        for (var index = 3; index < pixels.Length; index += 4)
        {
            pixels[index] = 255;
        }

        var repaired = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        repaired.Freeze();
        return repaired;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Detach();
    }
}
