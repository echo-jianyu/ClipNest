using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

namespace ClipNest.Services;

public sealed class ClipboardService : IClipboardService
{
    private const int MaxImageDimension = 2560;
    private const int CaptureAttempts = 10;
    private const int CaptureRetryDelayMilliseconds = 75;
    private const int ClipboardPollIntervalMilliseconds = 1000;
    private const string InternalCopyFormat = "ClipNest.InternalCopy";

    private readonly object _copyStateLock = new();
    private readonly object _clipboardThreadLock = new();
    private IntPtr _windowHandle;
    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _captureTimer;
    private DispatcherTimer? _clipboardPollTimer;
    private uint? _ignoredSequence;
    private uint? _lastObservedSequence;
    private uint? _pendingCaptureSequence;
    private string? _ownCopySignature;
    private DateTime _ownCopyExpiryUtc;
    private bool _copyWriteInProgress;
    private CancellationTokenSource? _captureCancellation;
    private Thread? _clipboardThread;
    private Dispatcher? _clipboardDispatcher;
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

        EnsureClipboardThread();
        _lastObservedSequence = NativeMethods.GetClipboardSequenceNumber();
        _pendingCaptureSequence = _lastObservedSequence;
        StartClipboardPolling();
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
        _clipboardPollTimer?.Stop();
        _clipboardPollTimer = null;
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        _lastObservedSequence = null;
        _pendingCaptureSequence = null;
        NativeMethods.RemoveClipboardFormatListener(_windowHandle);
        _source?.RemoveHook(WindowMessageHook);
        _source = null;
        _dispatcher = null;
        _windowHandle = IntPtr.Zero;
    }

    public Task<bool> CopyAsync(ClipItem item, bool includeFormatting)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClipboardService));
        }

        lock (_copyStateLock)
        {
            _ownCopySignature = item.Signature;
            _ownCopyExpiryUtc = DateTime.UtcNow.AddSeconds(2);
            _copyWriteInProgress = true;
        }

        return RunOnClipboardThreadAsync(
            () =>
            {
                try
                {
                    return CopyCore(item, includeFormatting);
                }
                finally
                {
                    lock (_copyStateLock)
                    {
                        _copyWriteInProgress = false;
                    }
                }
            });
    }

    private bool CopyCore(ClipItem item, bool includeFormatting)
    {
        var sequenceBefore = NativeMethods.GetClipboardSequenceNumber();

        var succeeded = false;
        try
        {
            var dataObject = BuildDataObject(item, includeFormatting);
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
            var sequenceAfter = NativeMethods.GetClipboardSequenceNumber();
            if (DidClipboardChange(sequenceBefore, sequenceAfter))
            {
                // Some clipboard providers report a COM error after already
                // publishing the content. A changed sequence means the copy succeeded.
                SetIgnoredSequence(sequenceAfter);
                succeeded = true;
            }
            else if (!succeeded && ClipboardContains(item))
            {
                // A few delayed-rendering providers omit a sequence update even
                // though the requested content is already available.
                SetIgnoredSequence(sequenceAfter);
                succeeded = true;
            }
            else if (!succeeded)
            {
                ClearOwnCopy();
            }
        }

        return succeeded;
    }

    private Task<T> RunOnClipboardThreadAsync<T>(Func<T> operation)
    {
        var dispatcher = EnsureClipboardThread();
        var completion = new TaskCompletionSource<T>();
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }));
        return completion.Task;
    }

    private Dispatcher EnsureClipboardThread()
    {
        lock (_clipboardThreadLock)
        {
            if (_clipboardDispatcher is not null)
            {
                return _clipboardDispatcher;
            }

            using var ready = new ManualResetEventSlim();
            _clipboardThread = new Thread(() =>
            {
                _clipboardDispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "ClipNest Clipboard Writer"
            };
            _clipboardThread.SetApartmentState(ApartmentState.STA);
            _clipboardThread.Start();
            ready.Wait();
            return _clipboardDispatcher
                ?? throw new InvalidOperationException(
                    "Clipboard worker thread failed to start.");
        }
    }

    private void ShutdownClipboardThread()
    {
        Dispatcher? dispatcher;
        Thread? thread;
        lock (_clipboardThreadLock)
        {
            dispatcher = _clipboardDispatcher;
            thread = _clipboardThread;
            _clipboardDispatcher = null;
            _clipboardThread = null;
        }

        dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(1));
        }
    }

    private void SetIgnoredSequence(uint sequence)
    {
        lock (_copyStateLock)
        {
            _ignoredSequence = sequence;
        }
    }

    private bool TryConsumeIgnoredSequence(uint sequence)
    {
        lock (_copyStateLock)
        {
            if (_ignoredSequence != sequence)
            {
                _ignoredSequence = null;
                return false;
            }

            _ignoredSequence = null;
            return true;
        }
    }

    private bool IsClipboardWriteInProgress()
    {
        lock (_copyStateLock)
        {
            return _copyWriteInProgress;
        }
    }

    private void ClearOwnCopy()
    {
        lock (_copyStateLock)
        {
            _ownCopySignature = null;
            _ownCopyExpiryUtc = DateTime.MinValue;
        }
    }

    private static WpfDataObject BuildDataObject(
        ClipItem item,
        bool includeFormatting)
    {
        var dataObject = BuildContentDataObject(item, includeFormatting);
        dataObject.SetData(InternalCopyFormat, "ClipNest");
        return dataObject;
    }

    internal static WpfDataObject BuildDragDataObject(ClipItem item)
    {
        var dataObject = BuildContentDataObject(item, includeFormatting: true);
        if (item.Kind == ClipContentKind.Image)
        {
            var bytes = item.GetImageBytes()
                ?? throw new InvalidOperationException("Image data is missing.");
            var imagePath = item.ImageFilePath;
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                imagePath = EnsureDragImageFile(bytes, item.Signature);
            }

            dataObject.SetData(WpfDataFormats.FileDrop, new[] { imagePath });
        }

        return dataObject;
    }

    private static WpfDataObject BuildContentDataObject(
        ClipItem item,
        bool includeFormatting)
    {
        var dataObject = new WpfDataObject();

        if (item.Kind == ClipContentKind.Image)
        {
            var bytes = item.GetImageBytes()
                ?? throw new InvalidOperationException("Image data is missing.");
            var image = DecodeImage(bytes);
            dataObject.SetData(WpfDataFormats.Bitmap, image);

            // Delayed-rendering clipboard providers may keep this stream alive
            // after SetDataObject returns, so it must not be disposed here.
            var pngStream = new MemoryStream(bytes, writable: false);
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

    private static string EnsureDragImageFile(byte[] bytes, string signature)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ClipNest", "drag");
        Directory.CreateDirectory(directory);

        var fileName = GetImageHash(signature, bytes);
        var path = Path.Combine(directory, $"{fileName}.png");
        if (File.Exists(path))
        {
            return path;
        }

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            try
            {
                File.Move(temporaryPath, path);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                return path;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string GetImageHash(string signature, byte[] bytes)
    {
        const string imagePrefix = "Image:";
        if (signature.StartsWith(imagePrefix, StringComparison.Ordinal)
            && signature.Length == imagePrefix.Length + 64
            && signature.Skip(imagePrefix.Length).All(Uri.IsHexDigit))
        {
            return signature.Substring(imagePrefix.Length).ToLowerInvariant();
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
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
        ObserveClipboardSequence(sequence);

        handled = true;
        return IntPtr.Zero;
    }

    private void StartClipboardPolling()
    {
        if (_dispatcher is null || _clipboardPollTimer is not null)
        {
            return;
        }

        _clipboardPollTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(
                ClipboardPollIntervalMilliseconds)
        };
        _clipboardPollTimer.Tick += (_, _) => PollClipboardSequence();
        _clipboardPollTimer.Start();
    }

    private void PollClipboardSequence()
    {
        if (_disposed || _dispatcher is null || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var sequence = NativeMethods.GetClipboardSequenceNumber();
        if (sequence != _lastObservedSequence)
        {
            ObserveClipboardSequence(sequence);
            return;
        }

        if (_pendingCaptureSequence == sequence)
        {
            // Clipboard providers can publish delayed data after a transient
            // window closes, so retry until the pending sequence is resolved.
            ScheduleClipboardCapture();
        }
    }

    private void ObserveClipboardSequence(uint sequence)
    {
        if (sequence == _lastObservedSequence)
        {
            return;
        }

        _lastObservedSequence = sequence;
        var isWriteInProgress = IsClipboardWriteInProgress();
        var ignored = !isWriteInProgress
            && TryConsumeIgnoredSequence(sequence);
        if (isWriteInProgress || ignored)
        {
            return;
        }

        _pendingCaptureSequence = sequence;
        ScheduleClipboardCapture();
    }

    private void MarkCaptureComplete(uint sequence)
    {
        if (_pendingCaptureSequence == sequence)
        {
            _pendingCaptureSequence = null;
        }
    }

    private async Task CaptureClipboardAsync(
        uint sequence,
        CancellationToken cancellationToken)
    {
        if (_disposed || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        for (var attempt = 0; attempt < CaptureAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ClipboardSnapshot? snapshot = null;
            try
            {
                snapshot = await RunOnClipboardThreadAsync(
                        () => ReadClipboardSnapshot())
                    .ConfigureAwait(true);
                if (snapshot is null)
                {
                    MarkCaptureComplete(sequence);
                    return;
                }

                var content = await Task.Run(
                        () => BuildClipboardContent(snapshot, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(true);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (content is null)
                {
                    if (attempt < CaptureAttempts - 1)
                    {
                        await Task.Delay(
                                CaptureRetryDelayMilliseconds,
                                cancellationToken)
                            .ConfigureAwait(true);
                        continue;
                    }

                    MarkCaptureComplete(sequence);
                    return;
                }

                if (!IsOwnCopyEcho(content))
                {
                    ContentChanged?.Invoke(this, content);
                }

                MarkCaptureComplete(sequence);
                return;
            }
            catch (ExternalException) when (attempt < CaptureAttempts - 1)
            {
                await Task.Delay(
                        CaptureRetryDelayMilliseconds,
                        cancellationToken)
                    .ConfigureAwait(true);
            }
            catch (ExternalException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                MarkCaptureComplete(sequence);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                snapshot?.Dispose();
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

        _captureCancellation?.Cancel();
        if (_captureTimer is null)
        {
            _captureTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(140)
            };
            _captureTimer.Tick += (_, _) =>
            {
                _captureTimer.Stop();
                _captureCancellation?.Cancel();
                _captureCancellation?.Dispose();
                var sequence = _pendingCaptureSequence;
                if (sequence is null)
                {
                    _captureCancellation = null;
                    return;
                }

                _captureCancellation = new CancellationTokenSource();
                _ = CaptureClipboardAsync(
                    sequence.Value,
                    _captureCancellation.Token);
            };
        }

        _captureTimer.Stop();
        _captureTimer.Start();
    }

    private bool IsOwnCopyEcho(ClipContent content)
    {
        lock (_copyStateLock)
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
            using var snapshot = ReadClipboardSnapshot(ignoreInternalMarker: true);
            if (snapshot is null)
            {
                return false;
            }

            var content = BuildClipboardContent(snapshot, CancellationToken.None);
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

    private static ClipboardSnapshot? ReadClipboardSnapshot(bool ignoreInternalMarker = false)
    {
        var dataObject = WpfClipboard.GetDataObject();
        if (dataObject is null
            || (!ignoreInternalMarker && dataObject.GetDataPresent(InternalCopyFormat)))
        {
            return null;
        }

        var text = GetClipboardText(dataObject);
        var rtf = GetClipboardString(dataObject, WpfDataFormats.Rtf);
        var html = GetClipboardString(dataObject, WpfDataFormats.Html);
        var hasPng = dataObject.GetDataPresent("PNG", true);

        if (!hasPng && !string.IsNullOrWhiteSpace(text))
        {
            // Excel, Word and many rich-content tools publish a preview bitmap
            // alongside text. Text remains the expected clipboard payload there.
            return ClipboardSnapshot.FromText(text!, rtf, html);
        }

        if (hasPng)
        {
            var pngBytes = TryReadBytes(dataObject.GetData("PNG", true));
            if (pngBytes is not null)
            {
                return ClipboardSnapshot.FromPng(pngBytes, text, rtf, html);
            }
        }

        if (dataObject.GetDataPresent(WpfDataFormats.Bitmap, true)
            || dataObject.GetDataPresent(WpfDataFormats.Dib, true))
        {
            var image = WpfClipboard.GetImage();
            if (image is not null)
            {
                var bitmap = CopyToFrozenBitmap(image);
                var isUsable = IsUsableClipboardBitmap(bitmap);
                if (isUsable)
                {
                    return ClipboardSnapshot.FromBitmap(
                        bitmap,
                        text,
                        rtf,
                        html);
                }

                // Some DIB providers expose a valid-looking WPF InteropBitmap
                // whose pixels are unavailable, while the WinForms decoder
                // still reads the same clipboard image correctly.
            }
        }

        var formsImage = FormsClipboard.GetImage();
        if (formsImage is not null)
        {
            return ClipboardSnapshot.FromFormsImage(formsImage, text, rtf, html);
        }

        return string.IsNullOrWhiteSpace(text)
            ? null
            : ClipboardSnapshot.FromText(text!, rtf, html);
    }

    private static string? GetClipboardText(System.Windows.IDataObject dataObject)
    {
        var text = GetClipboardString(dataObject, WpfDataFormats.UnicodeText);
        return string.IsNullOrWhiteSpace(text)
            ? GetClipboardString(dataObject, WpfDataFormats.Text)
            : text;
    }

    private static string? GetClipboardString(System.Windows.IDataObject dataObject, string format)
    {
        try
        {
            return dataObject.GetDataPresent(format, true)
                ? dataObject.GetData(format, true) as string
                : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static BitmapSource CopyToFrozenBitmap(BitmapSource source)
    {
        if (source.CanFreeze)
        {
            source.Freeze();
        }

        if (source.IsFrozen)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap(
            source,
            System.Windows.Media.PixelFormats.Bgra32,
            destinationPalette: null,
            alphaThreshold: 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);
        copy.Freeze();
        return copy;
    }

    private static ClipContent? BuildClipboardContent(
        ClipboardSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!snapshot.IsImage)
        {
            return string.IsNullOrWhiteSpace(snapshot.Text)
                ? null
                : ClipContent.FromText(snapshot.Text!, snapshot.Rtf, snapshot.Html);
        }

        BitmapSource? source = null;
        try
        {
            if (snapshot.PngBytes is not null)
            {
                source = TryDecodeImage(snapshot.PngBytes);
            }

            if (source is null && snapshot.Bitmap is not null)
            {
                source = snapshot.Bitmap;
            }

            if (source is null && snapshot.FormsImage is not null)
            {
                using var stream = new MemoryStream();
                snapshot.FormsImage.Save(
                    stream,
                    System.Drawing.Imaging.ImageFormat.Png);
                source = TryDecodeImage(stream.ToArray());
            }
        }
        catch (Exception exception) when (
            exception is ExternalException
            or InvalidOperationException
            or ArgumentException)
        {
        }

        if (source is not null && IsUsableClipboardBitmap(source))
        {
            var normalized = NormalizeImage(source);
            return ClipContent.FromImage(
                normalized.Bytes,
                normalized.Width,
                normalized.Height);
        }

        return string.IsNullOrWhiteSpace(snapshot.Text)
            ? null
            : ClipContent.FromText(snapshot.Text!, snapshot.Rtf, snapshot.Html);
    }

    private sealed class ClipboardSnapshot : IDisposable
    {
        private ClipboardSnapshot(
            string? text,
            string? rtf,
            string? html,
            bool isImage,
            byte[]? pngBytes = null,
            BitmapSource? bitmap = null,
            System.Drawing.Image? formsImage = null)
        {
            Text = text;
            Rtf = rtf;
            Html = html;
            IsImage = isImage;
            PngBytes = pngBytes;
            Bitmap = bitmap;
            FormsImage = formsImage;
        }

        public string? Text { get; }

        public string? Rtf { get; }

        public string? Html { get; }

        public bool IsImage { get; }

        public byte[]? PngBytes { get; }

        public BitmapSource? Bitmap { get; }

        public System.Drawing.Image? FormsImage { get; }

        public static ClipboardSnapshot FromText(
            string text,
            string? rtf,
            string? html)
        {
            return new ClipboardSnapshot(text, rtf, html, isImage: false);
        }

        public static ClipboardSnapshot FromPng(
            byte[] pngBytes,
            string? text,
            string? rtf,
            string? html)
        {
            return new ClipboardSnapshot(
                text,
                rtf,
                html,
                isImage: true,
                pngBytes: pngBytes);
        }

        public static ClipboardSnapshot FromBitmap(
            BitmapSource bitmap,
            string? text,
            string? rtf,
            string? html)
        {
            return new ClipboardSnapshot(
                text,
                rtf,
                html,
                isImage: true,
                bitmap: bitmap);
        }

        public static ClipboardSnapshot FromFormsImage(
            System.Drawing.Image formsImage,
            string? text,
            string? rtf,
            string? html)
        {
            return new ClipboardSnapshot(
                text,
                rtf,
                html,
                isImage: true,
                formsImage: formsImage);
        }

        public void Dispose()
        {
            FormsImage?.Dispose();
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
            return HasVisiblePixels(DecodeImage(pngBytes));
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

    internal static bool HasVisiblePixels(BitmapSource source)
    {
        try
        {
            BitmapSource sampled = source;
            if (source.PixelWidth > 64 || source.PixelHeight > 64)
            {
                var scale = Math.Min(64d / source.PixelWidth, 64d / source.PixelHeight);
                var thumbnail = new TransformedBitmap(
                    source,
                    new System.Windows.Media.ScaleTransform(scale, scale));
                thumbnail.Freeze();
                sampled = thumbnail;
            }

            var converted = new FormatConvertedBitmap(
                sampled,
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
        catch (NotSupportedException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
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

    internal static bool IsUsableClipboardBitmap(BitmapSource? bitmap)
    {
        return bitmap is not null
            && bitmap.PixelWidth > 1
            && bitmap.PixelHeight > 1
            && HasVisiblePixels(bitmap);
    }

    internal static (byte[] Bytes, int Width, int Height) NormalizeImage(BitmapSource source)
    {
        BitmapSource output = RepairClipboardAlpha(source);
        var longestSide = Math.Max(source.PixelWidth, source.PixelHeight);

        if (longestSide > MaxImageDimension)
        {
            var scale = (double)MaxImageDimension / longestSide;
            var transformed = new TransformedBitmap(
                output,
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
        ShutdownClipboardThread();
    }
}
