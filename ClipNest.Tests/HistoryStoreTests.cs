using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Tests;

public sealed class HistoryStoreTests
{
    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    [Fact]
    public async Task SaveAndLoad_RoundTripsTextAndImageRecords()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "history.json");

        try
        {
            using var store = new HistoryStore(path);
            var text = ClipItem.Create(ClipContent.FromText(
                "round-trip text",
                @"{\rtf1 round-trip}",
                "<b>round-trip</b>"));
            text.Pinned = true;
            var image = ClipItem.Create(ClipContent.FromImage(
                Convert.FromBase64String(OnePixelPngBase64),
                1,
                1));

            await store.SaveAsync([text, image]);
            var loaded = await store.LoadAsync();

            Assert.Equal(2, loaded.Count);
            Assert.Equal("round-trip text", loaded[0].PlainText);
            Assert.Equal(@"{\rtf1 round-trip}", loaded[0].Rtf);
            Assert.True(loaded[0].Pinned);
            Assert.Equal(ClipContentKind.Image, loaded[1].Kind);
            Assert.Equal("图片", loaded[1].DisplayText);
            Assert.Equal("图片", loaded[1].TooltipText);
            Assert.Equal(1, loaded[1].PixelWidth);
            Assert.Equal(1, loaded[1].PixelHeight);
            Assert.NotNull(loaded[1].ImagePngBase64);
            Assert.NotNull(loaded[1].ImagePreview);
            Assert.True(File.Exists(Path.Combine(
                directory,
                "images",
                $"{image.Signature.Substring(6).ToLowerInvariant()}.png")));
            var historyJson = File.ReadAllText(path);
            Assert.DoesNotContain(
                "imagePreview",
                historyJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "imagePngBase64",
                historyJson,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("imageFileName", historyJson);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WithCorruptJson_ReturnsEmptyHistory()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "history.json");

        try
        {
            File.WriteAllText(path, "{not-valid-json");
            using var store = new HistoryStore(path);

            var loaded = await store.LoadAsync();

            Assert.Empty(loaded);
            Assert.NotNull(store.LastWarning);
            Assert.Single(Directory.GetFiles(directory, "history.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WithVersion1Json_MigratesExistingRecord()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "history.json");

        try
        {
            File.WriteAllText(
                path,
                """
                [
                  {
                    "id": "f63a7331-a248-47cc-a242-13adc56cb8e8",
                    "kind": "Text",
                    "signature": "Text:LEGACY",
                    "plainText": "legacy record",
                    "rtf": null,
                    "html": null,
                    "imagePngBase64": null,
                    "pixelWidth": 0,
                    "pixelHeight": 0,
                    "updatedAt": "2026-11-23T08:34:00+08:00",
                    "pinned": true,
                    "displayText": "legacy record",
                    "isText": true
                  }
                ]
                """);
            using var store = new HistoryStore(path);

            var loaded = await store.LoadAsync();

            var item = Assert.Single(loaded);
            Assert.Equal("legacy record", item.PlainText);
            Assert.True(item.Pinned);
            Assert.Equal(
                new DateTimeOffset(2026, 11, 23, 8, 34, 0, TimeSpan.FromHours(8)),
                item.UpdatedAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_WithInvalidKind_IgnoresDamagedRecord()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "history.json");

        try
        {
            File.WriteAllText(
                path,
                """
                [
                  {
                    "id": "f63a7331-a248-47cc-a242-13adc56cb8e8",
                    "kind": 99,
                    "plainText": "zombie",
                    "updatedAtUtcTicks": 638000000000000000
                  },
                  {
                    "id": "8f80ea7a-f9ac-4f0d-b5ae-44a8e30d59ff",
                    "kind": 0,
                    "plainText": "valid",
                    "updatedAtUtcTicks": 638000000000000001
                  }
                ]
                """);
            using var store = new HistoryStore(path);

            var loaded = await store.LoadAsync();

            var item = Assert.Single(loaded);
            Assert.Equal("valid", item.PlainText);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Save_RemovesImagesNoLongerReferencedByHistory()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "history.json");

        try
        {
            using var store = new HistoryStore(path);
            var image = ClipItem.Create(ClipContent.FromImage(
                Convert.FromBase64String(OnePixelPngBase64),
                1,
                1));
            await store.SaveAsync([image]);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory, "images"), "*.png"));

            await store.SaveAsync([ClipItem.Create(ClipContent.FromText("text only"))]);

            Assert.Empty(Directory.GetFiles(Path.Combine(directory, "images"), "*.png"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Save_DoesNotOverwriteAnImageEditedWhileSnapshotIsPersisted()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "history.json");

        try
        {
            using var store = new HistoryStore(path);
            var originalContent = ClipContent.FromImage(
                EncodePng(20, 120, 110),
                2,
                2);
            var item = ClipItem.Create(originalContent);

            var saveTask = store.SaveAsync([item]);
            item.ReplaceImage(EncodePng(220, 40, 20), 3, 3);
            var result = await saveTask;

            var externalized = Assert.Single(result.ExternalizedImages);
            Assert.False(item.TryAttachExternalImage(
                externalized.ImagePath,
                externalized.Signature,
                externalized.InlineImagePngBase64));
            Assert.Equal(
                ClipContent.FromImage(EncodePng(220, 40, 20), 3, 3).Signature,
                item.Signature);

            var loaded = Assert.Single(await store.LoadAsync());
            Assert.Equal(originalContent.Signature, loaded.Signature);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] EncodePng(byte red, byte green, byte blue)
    {
        var pixels = new byte[2 * 2 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = red;
            pixels[index + 1] = green;
            pixels[index + 2] = blue;
            pixels[index + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            2 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ClipNest.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
