using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Infrastructure;

namespace ClipNest.Models;

public sealed class ClipItem : ObservableObject
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static long _lastSequence = DateTime.UtcNow.Ticks;

    private bool _pinned;
    private string _plainText = string.Empty;
    private string? _rtf;
    private string? _html;
    private string? _imagePngBase64;
    private ImageSource? _imagePreview;

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
        get => _imagePngBase64;
        private set
        {
            if (SetProperty(ref _imagePngBase64, value))
            {
                _imagePreview = null;
                OnPropertyChanged(nameof(ImagePreview));
            }
        }
    }

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

    public bool IsText => Kind == ClipContentKind.Text;

    public bool IsImage => Kind == ClipContentKind.Image;

    public string DisplayText
    {
        get
        {
            if (Kind == ClipContentKind.Image)
            {
                return "图片";
            }

            var normalized = WhitespaceRegex.Replace(PlainText, " ").Trim();
            return normalized.Length <= 40
                ? normalized
                : $"{normalized.Substring(0, 40)}…";
        }
    }

    public string TooltipText
    {
        get
        {
            if (Kind == ClipContentKind.Image)
            {
                return "图片";
            }

            return PlainText.Length <= 2000
                ? PlainText
                : $"{PlainText.Substring(0, 2000)}…";
        }
    }

    public string PinLabel => Pinned ? "取消置顶" : "置顶";

    public ImageSource? ImagePreview
    {
        get
        {
            if (Kind != ClipContentKind.Image || _imagePreview is not null || ImagePngBase64 is null)
            {
                return _imagePreview;
            }

            try
            {
                var bytes = Convert.FromBase64String(ImagePngBase64);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 640;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                bitmap.Freeze();
                _imagePreview = bitmap;
            }
            catch (FormatException)
            {
                _imagePreview = null;
            }
            catch (NotSupportedException)
            {
                _imagePreview = null;
            }

            return _imagePreview;
        }
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
        ImagePngBase64 = content.ImagePng is null ? null : Convert.ToBase64String(content.ImagePng);
        PixelWidth = content.PixelWidth;
        PixelHeight = content.PixelHeight;
        UpdatedAt = DateTimeOffset.Now;
        Sequence = NextSequence();
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(ImagePreview));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsImage));
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
        ImagePngBase64 = Convert.ToBase64String(pngBytes);
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Signature = content.Signature;
        UpdatedAt = DateTimeOffset.Now;
        Sequence = NextSequence();
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(ImagePreview));
    }

    public void NormalizeAfterLoad()
    {
        _imagePreview = null;
        OnPropertyChanged(nameof(ImagePreview));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsImage));
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
