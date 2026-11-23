using System.IO;
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
            Assert.Equal(1, loaded[1].PixelWidth);
            Assert.Equal(1, loaded[1].PixelHeight);
            Assert.NotNull(loaded[1].ImagePngBase64);
            Assert.NotNull(loaded[1].ImagePreview);
            Assert.DoesNotContain(
                "imagePreview",
                File.ReadAllText(path),
                StringComparison.OrdinalIgnoreCase);
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

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ClipNest.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
