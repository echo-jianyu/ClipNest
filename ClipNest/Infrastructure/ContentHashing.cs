using System.Security.Cryptography;
using System.Text;

namespace ClipNest.Infrastructure;

internal static class ContentHashing
{
    private const string ImageSignaturePrefix = "Image:";
    private const int Sha256HexLength = 64;

    internal static string ComputeSha256Hex(string text, bool lowerCase = false)
    {
        return ComputeSha256Hex(Encoding.UTF8.GetBytes(text), lowerCase);
    }

    internal static string ComputeSha256Hex(byte[] bytes, bool lowerCase = false)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        using var sha256 = SHA256.Create();
        return ToHex(sha256.ComputeHash(bytes), lowerCase);
    }

    internal static string ResolveImageHashHex(string signature, byte[] bytes)
    {
        if (bytes is null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        if (!string.IsNullOrEmpty(signature)
            && signature.StartsWith(ImageSignaturePrefix, StringComparison.Ordinal)
            && signature.Length == ImageSignaturePrefix.Length + Sha256HexLength)
        {
            var isHex = true;
            for (var index = ImageSignaturePrefix.Length; index < signature.Length; index++)
            {
                if (!Uri.IsHexDigit(signature[index]))
                {
                    isHex = false;
                    break;
                }
            }

            if (isHex)
            {
                return signature.Substring(ImageSignaturePrefix.Length).ToLowerInvariant();
            }
        }

        return ComputeSha256Hex(bytes, lowerCase: true);
    }

    internal static string CreateImageFileName(string signature, byte[] bytes)
    {
        return $"{ResolveImageHashHex(signature, bytes)}.png";
    }

    private static string ToHex(byte[] bytes, bool lowerCase)
    {
        var alphabet = lowerCase ? "0123456789abcdef" : "0123456789ABCDEF";
        var chars = new char[bytes.Length * 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = bytes[index];
            chars[index * 2] = alphabet[value >> 4];
            chars[(index * 2) + 1] = alphabet[value & 0x0F];
        }

        return new string(chars);
    }
}
