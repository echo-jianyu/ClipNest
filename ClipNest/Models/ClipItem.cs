using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Infrastructure;

namespace ClipNest.Models;

public sealed class ClipItem : ObservableObject
{
    public const double DefaultDisplayFontSize = 13.5;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly SemaphoreSlim ThumbnailDecodeGate = new(2, 2);
    private static long _lastSequence = DateTime.UtcNow.Ticks;

    private bool _pinned;
    private bool _isKeyboardSelected;
    private double _displayFontSize = DefaultDisplayFontSize;
    private string _plainText = string.Empty;
    private string _displayText = string.Empty;
    private string _tooltipText = string.Empty;
    private string? _rtf;
    private string? _html;
    private string? _imagePngBase64;
    private string? _imageFilePath;
    private ImageSource? _imagePreview;
    private bool _imagePreviewLoading;
    private bool _imagePreviewLoadAttempted;
    private int _imagePreviewVersion;

    public Guid Id { get; private set; } = Guid.NewGuid();

    public ClipContentKind Kind { get; private set; }

    public string Signature { get; private set; } = string.Empty;

    public string PlainText
    {
        get => _plainText;
        private set
        {
            if (SetProperty(ref _plainText, value))
            {
                RefreshPreviewText();
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(TooltipText));
            }
        }
    }

    public string? Rtf
    {
        get => _rtf;
        private set => SetProperty(ref _rtf, value);
    }

    public string? Html
    {
        get => _html;
        private set => SetProperty(ref _html, value);
    }

    public string? ImagePngBase64
    {
        get
        {
            if (_imagePngBase64 is not null)
            {
                return _imagePngBase64;
            }

            var bytes = GetImageBytes();
            return bytes is null ? null : Convert.ToBase64String(bytes);
        }
        private set => SetImageStorage(value, null);
    }

    internal string? InlineImagePngBase64 => _imagePngBase64;

    internal string? ImageFilePath => _imageFilePath;

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.Now;

    internal long Sequence { get; private set; }

    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (SetProperty(ref _pinned, value))
            {
                OnPropertyChanged(nameof(PinLabel));
            }
        }
    }

    public bool IsKeyboardSelected
    {
        get => _isKeyboardSelected;
        set => SetProperty(ref _isKeyboardSelected, value);
    }

    public bool IsText => Kind == ClipContentKind.Text;

    public bool IsImage => Kind == ClipContentKind.Image;

    public string DisplayText
    {
        get => _displayText;
    }

    public double DisplayFontSize
    {
        get => _displayFontSize;
        private set
        {
            if (SetProperty(ref _displayFontSize, value))
            {
                OnPropertyChanged(nameof(DisplayLineHeight));
                OnPropertyChanged(nameof(DisplayMaxHeight));
            }
        }
    }

    public double DisplayLineHeight => Math.Round(DisplayFontSize * 1.4, 1);

    public double DisplayMaxHeight => Math.Round(DisplayLineHeight * 2, 1);

    public string TooltipText
    {
        get => _tooltipText;
    }

    public string PinLabel => Pinned ? "取消置顶" : "置顶";

    public string ImageDimensionsLabel => IsImage
        ? $"{PixelWidth}×{PixelHeight}"
        : string.Empty;

    public ImageSource? ImagePreview
    {
        get
        {
            if (Kind != ClipContentKind.Image || _imagePreview is not null)
            {
                return _imagePreview;
            }

            if (!_imagePreviewLoading && !_imagePreviewLoadAttempted)
            {
                BeginImagePreviewLoad();
            }

            return _imagePreview;
        }
    }

    internal byte[]? GetImageBytes()
    {
        if (_imagePngBase64 is not null)
        {
            return Convert.FromBase64String(_imagePngBase64);
        }

        return _imageFilePath is not null && File.Exists(_imageFilePath)
            ? File.ReadAllBytes(_imageFilePath)
            : null;
    }

    internal void AttachImageFile(string imageFilePath, bool notify = true)
    {
        if (string.IsNullOrWhiteSpace(imageFilePath))
        {
            throw new ArgumentException("Image file path cannot be empty.", nameof(imageFilePath));
        }

        if (notify)
        {
            SetImageStorage(null, imageFilePath);
            return;
        }

        _imagePngBase64 = null;
        _imageFilePath = imageFilePath;
    }

    internal bool TryAttachExternalImage(
        string imageFilePath,
        string expectedSignature,
        string expectedInlineBase64)
    {
        if (Kind != ClipContentKind.Image
            || !string.Equals(Signature, expectedSignature, StringComparison.Ordinal)
            || !ReferenceEquals(_imagePngBase64, expectedInlineBase64))
        {
            return false;
        }

        SetImageStorage(null, imageFilePath);
        return true;
    }

    internal void ApplyDisplaySettings(double fontSize)
    {
        DisplayFontSize = Math.Max(1, fontSize);
    }

    public static ClipItem Create(ClipContent content)
    {
        var item = new ClipItem();
        item.Apply(content);
        return item;
    }

    internal static ClipItem Restore(
        Guid id,
        ClipContentKind kind,
        string plainText,
        string? rtf,
        string? html,
        string? imagePngBase64,
        string? imageFilePath,
        int pixelWidth,
        int pixelHeight,
        DateTimeOffset updatedAt,
        bool pinned,
        string? signature,
        long sequence)
    {
        var item = new ClipItem
        {
            Id = id,
            Kind = kind,
            PlainText = plainText ?? string.Empty,
            Rtf = rtf,
            Html = html,
            ImagePngBase64 = imagePngBase64,
            _imageFilePath = imageFilePath,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight,
            UpdatedAt = updatedAt,
            Pinned = pinned,
            Sequence = sequence > 0 ? sequence : NextSequence()
        };
        EnsureSequenceFloor(item.Sequence);

        if (!string.IsNullOrWhiteSpace(signature))
        {
            item.Signature = signature!;
        }
        else if (kind == ClipContentKind.Text)
        {
            item.Signature = ClipContent.FromText(item.PlainText).Signature;
        }
        else if (!string.IsNullOrWhiteSpace(imagePngBase64))
        {
            item.Signature = ClipContent.FromImage(
                Convert.FromBase64String(imagePngBase64),
                pixelWidth,
                pixelHeight).Signature;
        }
        else if (imageFilePath is not null)
        {
            var bytes = item.GetImageBytes();
            if (bytes is not null)
            {
                item.Signature = ClipContent.FromImage(
                    bytes,
                    pixelWidth,
                    pixelHeight).Signature;
            }
        }

        item.EnsureImageDimensions();
        item.NormalizeAfterLoad();
        return item;
    }

    public void Apply(ClipContent content)
    {
        Kind = content.Kind;
        Signature = content.Signature;
        PlainText = content.PlainText;
        Rtf = content.Rtf;
        Html = content.Html;
        SetImageStorage(
            content.ImagePng is null ? null : Convert.ToBase64String(content.ImagePng),
            null);
        PixelWidth = content.PixelWidth;
        PixelHeight = content.PixelHeight;
        RefreshPreviewText();
        UpdatedAt = DateTimeOffset.Now;
        Sequence = NextSequence();
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(ImagePreview));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(PixelHeight));
        OnPropertyChanged(nameof(ImageDimensionsLabel));
    }

    public void UpdatePlainText(string text)
    {
        PlainText = text;
        Rtf = null;
        Html = null;
        Signature = ClipContent.FromText(text).Signature;
        UpdatedAt = DateTimeOffset.Now;
        Sequence = NextSequence();
        OnPropertyChanged(nameof(UpdatedAt));
    }

    public void ReplaceImage(byte[] pngBytes, int pixelWidth, int pixelHeight)
    {
        if (pngBytes is null)
        {
            throw new ArgumentNullException(nameof(pngBytes));
        }

        var content = ClipContent.FromImage(pngBytes, pixelWidth, pixelHeight);
        SetImageStorage(Convert.ToBase64String(pngBytes), null);
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Signature = content.Signature;
        UpdatedAt = DateTimeOffset.Now;
        Sequence = NextSequence();
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(ImagePreview));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(PixelHeight));
        OnPropertyChanged(nameof(ImageDimensionsLabel));
    }

    public void NormalizeAfterLoad()
    {
        RefreshPreviewText();
        ResetImagePreview();
        OnPropertyChanged(nameof(ImagePreview));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsImage));
        OnPropertyChanged(nameof(PixelWidth));
        OnPropertyChanged(nameof(PixelHeight));
        OnPropertyChanged(nameof(ImageDimensionsLabel));
    }

    private void SetImageStorage(string? inlineBase64, string? filePath)
    {
        if (string.Equals(_imagePngBase64, inlineBase64, StringComparison.Ordinal)
            && string.Equals(_imageFilePath, filePath, StringComparison.Ordinal))
        {
            return;
        }

        _imagePngBase64 = inlineBase64;
        _imageFilePath = filePath;
        ResetImagePreview();
        OnPropertyChanged(nameof(ImagePngBase64));
        OnPropertyChanged(nameof(ImagePreview));
    }

    private void RefreshPreviewText()
    {
        if (Kind == ClipContentKind.Image)
        {
            _displayText = "图片";
            _tooltipText = "图片";
            return;
        }

        var normalized = WhitespaceRegex.Replace(PlainText, " ").Trim();
        _displayText = normalized;
        _tooltipText = PlainText.Length <= 2000
            ? PlainText
            : $"{PlainText.Substring(0, 2000)}…";
    }

    private void BeginImagePreviewLoad()
    {
        _imagePreviewLoadAttempted = true;
        var applicationDispatcher = System.Windows.Application.Current?.Dispatcher;
        if (applicationDispatcher is null)
        {
            try
            {
                TryApplyImagePreview(
                    DecodeThumbnail(GetImageBytes()),
                    _imagePreviewVersion);
            }
            catch (FormatException)
            {
                TryApplyImagePreview(null, _imagePreviewVersion);
            }
            catch (IOException)
            {
                TryApplyImagePreview(null, _imagePreviewVersion);
            }

            return;
        }

        _imagePreviewLoading = true;
        var version = _imagePreviewVersion;
        var inlineBase64 = _imagePngBase64;
        var filePath = _imageFilePath;
        _ = LoadImagePreviewAsync(
            inlineBase64,
            filePath,
            version,
            applicationDispatcher);
    }

    private async Task LoadImagePreviewAsync(
        string? inlineBase64,
        string? filePath,
        int version,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        ImageSource? preview = null;
        await ThumbnailDecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[]? bytes = null;
            if (inlineBase64 is not null)
            {
                bytes = Convert.FromBase64String(inlineBase64);
            }
            else if (filePath is not null && File.Exists(filePath))
            {
                bytes = File.ReadAllBytes(filePath);
            }

            preview = DecodeThumbnail(bytes);
        }
        catch (FormatException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }
        finally
        {
            ThumbnailDecodeGate.Release();
        }

        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        await dispatcher.InvokeAsync(
            new Action(() =>
            {
                if (version != _imagePreviewVersion)
                {
                    return;
                }

                TryApplyImagePreview(preview, version);
            }));
    }

    private void TryApplyImagePreview(ImageSource? preview, int version)
    {
        if (version != _imagePreviewVersion)
        {
            return;
        }

        _imagePreviewLoading = false;
        _imagePreview = preview;
        OnPropertyChanged(nameof(ImagePreview));
    }

    internal static BitmapImage? DecodeThumbnail(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var longestSide = Math.Max(frame.PixelWidth, frame.PixelHeight);

            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (longestSide > 1280)
            {
                if (frame.PixelWidth >= frame.PixelHeight)
                {
                    bitmap.DecodePixelWidth = 1280;
                }
                else
                {
                    bitmap.DecodePixelHeight = 1280;
                }
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (FormatException)
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

    private void ResetImagePreview()
    {
        _imagePreview = null;
        _imagePreviewLoading = false;
        _imagePreviewLoadAttempted = false;
        unchecked
        {
            _imagePreviewVersion++;
        }
    }

    private void EnsureImageDimensions()
    {
        if (Kind != ClipContentKind.Image
            || (PixelWidth > 0 && PixelHeight > 0))
        {
            return;
        }

        var bytes = GetImageBytes();
        if (bytes is null || bytes.Length == 0)
        {
            PixelWidth = Math.Max(1, PixelWidth);
            PixelHeight = Math.Max(1, PixelHeight);
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (frame.PixelWidth > 0 && frame.PixelHeight > 0)
            {
                PixelWidth = frame.PixelWidth;
                PixelHeight = frame.PixelHeight;
                return;
            }
        }
        catch (FormatException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
        }

        PixelWidth = Math.Max(1, PixelWidth);
        PixelHeight = Math.Max(1, PixelHeight);
    }

    private static long NextSequence()
    {
        return Interlocked.Increment(ref _lastSequence);
    }

    private static void EnsureSequenceFloor(long sequence)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _lastSequence);
            if (sequence <= current
                || Interlocked.CompareExchange(ref _lastSequence, sequence, current) == current)
            {
                return;
            }
        }
    }
}
