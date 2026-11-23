using System.Security.Cryptography;
using System.Text;

namespace ClipNest.Models;

public enum ClipContentKind
{
    Text,
    Image
}

public sealed class ClipContent
{
    private ClipContent()
    {
    }

    public ClipContentKind Kind { get; private set; }

    public string PlainText { get; private set; } = string.Empty;

    public string? Rtf { get; private set; }

    public string? Html { get; private set; }

    public byte[]? ImagePng { get; private set; }

    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public string Signature { get; private set; } = string.Empty;

    public static ClipContent FromText(string text, string? rtf = null, string? html = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        }

        var content = new ClipContent
        {
            Kind = ClipContentKind.Text,
            PlainText = text,
            Rtf = string.IsNullOrWhiteSpace(rtf) ? null : rtf,
            Html = string.IsNullOrWhiteSpace(html) ? null : html
        };

        content.Signature = CreateSignature(ClipContentKind.Text, text);
        return content;
    }

    public static ClipContent FromImage(byte[] pngBytes, int pixelWidth, int pixelHeight)
    {
        if (pngBytes is null)
        {
            throw new ArgumentNullException(nameof(pngBytes));
        }

        if (pngBytes.Length == 0)
        {
            throw new ArgumentException("Image data cannot be empty.", nameof(pngBytes));
        }

        var content = new ClipContent
        {
            Kind = ClipContentKind.Image,
            PlainText = string.Empty,
            ImagePng = pngBytes,
            PixelWidth = pixelWidth,
            PixelHeight = pixelHeight
        };

        content.Signature = CreateSignature(ClipContentKind.Image, pngBytes);
        return content;
    }

    private static string CreateSignature(ClipContentKind kind, string text)
    {
        return CreateSignature(kind, Encoding.UTF8.GetBytes(text));
    }

    private static string CreateSignature(ClipContentKind kind, byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return $"{kind}:{BitConverter.ToString(hash).Replace("-", string.Empty)}";
    }
}
