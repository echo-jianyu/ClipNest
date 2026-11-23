using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using ClipNest.Infrastructure;
using ClipNest.Models;

namespace ClipNest.Services;

public sealed class HistoryStore : IDisposable
{
    private readonly string _path;
    private readonly string _imagesDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastSaveVersion;
    private long _lastWrittenVersion;

    public HistoryStore(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("History path has no parent directory.", nameof(path));
        _imagesDirectory = Path.Combine(directory, "images");
    }

    public string? LastWarning { get; private set; }

    public async Task<IReadOnlyList<ClipItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<ClipItem>();
        }

        IReadOnlyList<ClipItem> items;
        try
        {
            var json = await AtomicFile
                .ReadAllBytesAsync(_path, cancellationToken)
                .ConfigureAwait(false);
            items = await Task.Run(() => Deserialize(json), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or SerializationException
            or System.Xml.XmlException)
        {
            LastWarning = "历史记录读取失败，已从空历史开始。";
            TryBackupCorruptFile();
            return Array.Empty<ClipItem>();
        }

        try
        {
            await MigrateInlineImagesAsync(items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            LastWarning = "历史图片迁移失败，本次仍可继续使用旧记录。";
        }

        return items;
    }

    public async Task SaveAsync(
        IReadOnlyList<ClipItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        var saveVersion = Interlocked.Increment(ref _lastSaveVersion);
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (saveVersion < Interlocked.Read(ref _lastWrittenVersion))
            {
                return;
            }

            var referencedImages = await ExternalizeImagesAsync(items, cancellationToken)
                .ConfigureAwait(false);
            var records = items.Select(ToStoredItem).ToList();
            var json = await Task.Run(() => Serialize(records), cancellationToken)
                .ConfigureAwait(false);

            await AtomicFile
                .WriteAllBytesAsync(_path, json, cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _lastWrittenVersion, saveVersion);
            DeleteOrphanedImages(referencedImages);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        return SaveAsync(Array.Empty<ClipItem>(), cancellationToken);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
    }

    private static byte[] Serialize(IReadOnlyList<StoredClipItem> records)
    {
        var serializer = new DataContractJsonSerializer(typeof(List<StoredClipItem>));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, records);
        return stream.ToArray();
    }

    private List<ClipItem> Deserialize(byte[] json)
    {
        try
        {
            var records = ReadObject<List<StoredClipItem>>(json);
            return RestoreStoredItems(records);
        }
        catch (SerializationException)
        {
            var records = ReadObject<List<LegacyClipItem>>(json);
            return RestoreLegacyItems(records);
        }
    }

    private static T ReadObject<T>(byte[] json)
        where T : class
    {
        var serializer = new DataContractJsonSerializer(typeof(T));
        using var stream = new MemoryStream(json, writable: false);
        return serializer.ReadObject(stream) as T
            ?? throw new SerializationException("The history document is empty.");
    }

    private List<ClipItem> RestoreStoredItems(
        IEnumerable<StoredClipItem> records)
    {
        var recordList = records.ToList();
        var items = new List<ClipItem>();
        for (var index = 0; index < recordList.Count; index++)
        {
            var record = recordList[index];
            var updatedAt = record.UpdatedAtUtcTicks > 0
                ? new DateTimeOffset(record.UpdatedAtUtcTicks, TimeSpan.Zero)
                : DateTimeOffset.Now;
            var sequence = record.Sequence > 0
                ? record.Sequence
                : updatedAt.UtcDateTime.Ticks + (recordList.Count - index);
            if (!Enum.IsDefined(typeof(ClipContentKind), record.Kind))
            {
                continue;
            }

            var item = TryRestore(
                record.Id,
                (ClipContentKind)record.Kind,
                record.PlainText,
                record.Rtf,
                record.Html,
                record.ImagePngBase64,
                ResolveImageFilePath(record.ImageFileName),
                record.PixelWidth,
                record.PixelHeight,
                updatedAt,
                record.Pinned,
                record.Signature,
                sequence);

            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private List<ClipItem> RestoreLegacyItems(
        IEnumerable<LegacyClipItem> records)
    {
        var recordList = records.ToList();
        var items = new List<ClipItem>();
        for (var index = 0; index < recordList.Count; index++)
        {
            var record = recordList[index];
            if (!Enum.TryParse(record.Kind, ignoreCase: true, out ClipContentKind kind)
                || !Enum.IsDefined(typeof(ClipContentKind), kind))
            {
                continue;
            }

            var updatedAt = DateTimeOffset.TryParse(
                record.UpdatedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : DateTimeOffset.Now;

            var item = TryRestore(
                record.Id,
                kind,
                record.PlainText,
                record.Rtf,
                record.Html,
                record.ImagePngBase64,
                ResolveImageFilePath(record.ImageFileName),
                record.PixelWidth,
                record.PixelHeight,
                updatedAt,
                record.Pinned,
                record.Signature,
                updatedAt.UtcDateTime.Ticks + (recordList.Count - index));

            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static ClipItem? TryRestore(
        Guid id,
        ClipContentKind kind,
        string? plainText,
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
        if (!Enum.IsDefined(typeof(ClipContentKind), kind))
        {
            return null;
        }

        if (kind == ClipContentKind.Text && string.IsNullOrWhiteSpace(plainText))
        {
            return null;
        }

        if (kind == ClipContentKind.Image
            && string.IsNullOrWhiteSpace(imagePngBase64)
            && imageFilePath is null)
        {
            return null;
        }

        try
        {
            return ClipItem.Restore(
                id == Guid.Empty ? Guid.NewGuid() : id,
                kind,
                plainText ?? string.Empty,
                rtf,
                html,
                imagePngBase64,
                imageFilePath,
                pixelWidth,
                pixelHeight,
                updatedAt,
                pinned,
                signature,
                sequence);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static StoredClipItem ToStoredItem(ClipItem item)
    {
        return new StoredClipItem
        {
            Id = item.Id,
            Kind = (int)item.Kind,
            PlainText = item.PlainText,
            Rtf = item.Rtf,
            Html = item.Html,
            ImageFileName = item.ImageFilePath is null
                ? null
                : Path.GetFileName(item.ImageFilePath),
            PixelWidth = item.PixelWidth,
            PixelHeight = item.PixelHeight,
            UpdatedAtUtcTicks = item.UpdatedAt.UtcDateTime.Ticks,
            Pinned = item.Pinned,
            Signature = item.Signature,
            Sequence = item.Sequence
        };
    }

    private async Task MigrateInlineImagesAsync(
        IReadOnlyList<ClipItem> items,
        CancellationToken cancellationToken)
    {
        if (!items.Any(item => item.IsImage && item.InlineImagePngBase64 is not null))
        {
            return;
        }

        await SaveAsync(items, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ExternalizeImagesAsync(
        IReadOnlyList<ClipItem> items,
        CancellationToken cancellationToken)
    {
        var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (!item.IsImage)
            {
                continue;
            }

            var existingPath = item.ImageFilePath;
            if (existingPath is not null
                && File.Exists(existingPath)
                && IsInsideImagesDirectory(existingPath))
            {
                referencedImages.Add(Path.GetFileName(existingPath));
                continue;
            }

            byte[]? bytes;
            try
            {
                bytes = item.GetImageBytes();
            }
            catch (FormatException)
            {
                continue;
            }

            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            var fileName = CreateImageFileName(item.Signature, bytes);
            var imagePath = Path.Combine(_imagesDirectory, fileName);
            referencedImages.Add(fileName);

            if (!File.Exists(imagePath))
            {
                await AtomicFile
                    .WriteAllBytesAsync(imagePath, bytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            item.AttachImageFile(imagePath, notify: false);
        }

        return referencedImages;
    }

    private bool IsInsideImagesDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return directory is not null
            && string.Equals(
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(_imagesDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateImageFileName(string signature, byte[] bytes)
    {
        const string imagePrefix = "Image:";
        if (signature.StartsWith(imagePrefix, StringComparison.Ordinal)
            && signature.Length == imagePrefix.Length + 64
            && signature.Skip(imagePrefix.Length).All(Uri.IsHexDigit))
        {
            return $"{signature.Substring(imagePrefix.Length).ToLowerInvariant()}.png";
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return $"{BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant()}.png";
    }

    private void DeleteOrphanedImages(HashSet<string> referencedImages)
    {
        if (!Directory.Exists(_imagesDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_imagesDirectory, "*.png"))
        {
            if (referencedImages.Contains(Path.GetFileName(path)))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private string? ResolveImageFilePath(string? imageFileName)
    {
        if (string.IsNullOrWhiteSpace(imageFileName))
        {
            return null;
        }

        var safeName = Path.GetFileName(imageFileName);
        if (!string.Equals(safeName, imageFileName, StringComparison.Ordinal)
            || !string.Equals(Path.GetExtension(safeName), ".png", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = Path.Combine(_imagesDirectory, safeName);
        return File.Exists(path) ? path : null;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backupPath = $"{_path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_path, backupPath, overwrite: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DataContract]
    private sealed class StoredClipItem
    {
        [DataMember(Name = "id", Order = 0)]
        public Guid Id { get; set; }

        [DataMember(Name = "kind", Order = 1)]
        public int Kind { get; set; }

        [DataMember(Name = "signature", Order = 2)]
        public string? Signature { get; set; }

        [DataMember(Name = "plainText", Order = 3)]
        public string? PlainText { get; set; }

        [DataMember(Name = "rtf", Order = 4)]
        public string? Rtf { get; set; }

        [DataMember(Name = "html", Order = 5)]
        public string? Html { get; set; }

        [DataMember(Name = "imageFileName", Order = 6)]
        public string? ImageFileName { get; set; }

        [DataMember(Name = "imagePngBase64", Order = 12, EmitDefaultValue = false)]
        public string? ImagePngBase64 { get; set; }

        [DataMember(Name = "pixelWidth", Order = 7)]
        public int PixelWidth { get; set; }

        [DataMember(Name = "pixelHeight", Order = 8)]
        public int PixelHeight { get; set; }

        [DataMember(Name = "updatedAtUtcTicks", Order = 9)]
        public long UpdatedAtUtcTicks { get; set; }

        [DataMember(Name = "pinned", Order = 10)]
        public bool Pinned { get; set; }

        [DataMember(Name = "sequence", Order = 11)]
        public long Sequence { get; set; }
    }

    [DataContract]
    private sealed class LegacyClipItem
    {
        [DataMember(Name = "id")]
        public Guid Id { get; set; }

        [DataMember(Name = "kind")]
        public string? Kind { get; set; }

        [DataMember(Name = "signature")]
        public string? Signature { get; set; }

        [DataMember(Name = "plainText")]
        public string? PlainText { get; set; }

        [DataMember(Name = "rtf")]
        public string? Rtf { get; set; }

        [DataMember(Name = "html")]
        public string? Html { get; set; }

        [DataMember(Name = "imagePngBase64")]
        public string? ImagePngBase64 { get; set; }

        [DataMember(Name = "imageFileName")]
        public string? ImageFileName { get; set; }

        [DataMember(Name = "pixelWidth")]
        public int PixelWidth { get; set; }

        [DataMember(Name = "pixelHeight")]
        public int PixelHeight { get; set; }

        [DataMember(Name = "updatedAt")]
        public string? UpdatedAt { get; set; }

        [DataMember(Name = "pinned")]
        public bool Pinned { get; set; }
    }
}
