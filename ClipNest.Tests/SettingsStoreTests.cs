using System.IO;
using ClipNest.Models;
using ClipNest.Services;

namespace ClipNest.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task Load_WithVersion1Json_RestoresPlacement()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "settings.json");

        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "width": 420,
                  "height": 280,
                  "left": 120,
                  "top": 64
                }
                """);
            var store = new SettingsStore(path);

            var settings = await store.LoadAsync();

            Assert.Equal(450, settings.Width);
            Assert.Equal(300, settings.Height);
            Assert.Equal(120, settings.Left);
            Assert.Equal(64, settings.Top);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSettings()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                Width = 480,
                Left = 88,
                Top = 32
            });

            var settings = await store.LoadAsync();

            Assert.Equal(480, settings.Width);
            Assert.Equal(320, settings.Height);
            Assert.Equal(88, settings.Left);
            Assert.Equal(32, settings.Top);
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
