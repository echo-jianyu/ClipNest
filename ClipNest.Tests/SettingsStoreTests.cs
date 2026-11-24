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

            Assert.Equal(400, settings.Width);
            Assert.Equal(400, settings.Height);
            Assert.Equal(120, settings.Left);
            Assert.Equal(64, settings.Top);
            Assert.Equal(HotKeyDefinition.Default.Modifiers, settings.HotKeyModifiers);
            Assert.Equal(HotKeyDefinition.Default.VirtualKey, settings.HotKeyKey);
            Assert.False(settings.HideAfterCopy);
            Assert.Equal(40, settings.CardTextLimit);
            Assert.Equal(13.5, settings.CardFontSize);
            Assert.False(settings.AutoStartOnLogin);
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
                Top = 32,
                HotKeyModifiers = HotKeyDefinition.ControlModifier,
                HotKeyKey = 0x56,
                HideAfterCopy = true,
                CardTextLimit = 72,
                CardFontSize = 15,
                AutoStartOnLogin = false
            });

            var settings = await store.LoadAsync();

            Assert.Equal(480, settings.Width);
            Assert.Equal(480, settings.Height);
            Assert.Equal(88, settings.Left);
            Assert.Equal(32, settings.Top);
            Assert.Equal(HotKeyDefinition.ControlModifier, settings.HotKeyModifiers);
            Assert.Equal(0x56u, settings.HotKeyKey);
            Assert.True(settings.HideAfterCopy);
            Assert.Equal(72, settings.CardTextLimit);
            Assert.Equal(15, settings.CardFontSize);
            Assert.False(settings.AutoStartOnLogin);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Save_UsesStableSnapshotWhenCallerMutatesSettings()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(path);
            var settings = new AppSettings
            {
                Width = 480,
                Left = 88,
                Top = 32
            };

            var saveTask = store.SaveAsync(settings);
            settings.Width = 900;
            settings.Left = 700;
            await saveTask;

            var loaded = await store.LoadAsync();
            Assert.Equal(480, loaded.Width);
            Assert.Equal(88, loaded.Left);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSaves_KeepTheLatestRequestedSnapshot()
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, "settings.json");

        try
        {
            var store = new SettingsStore(path);
            var first = new AppSettings { Left = 10 };
            var second = new AppSettings { Left = 20 };

            await Task.WhenAll(
                store.SaveAsync(first),
                store.SaveAsync(second));

            var loaded = await store.LoadAsync();
            Assert.Equal(20, loaded.Left);
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
