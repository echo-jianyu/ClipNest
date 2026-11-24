using System.IO;
using ClipNest.Services;
using Microsoft.Win32;

namespace ClipNest.Tests;

public sealed class AutoStartServiceTests
{
    [Fact]
    public void TrySetEnabled_WritesAndRemovesQuotedExecutablePath()
    {
        var registryPath =
            $@"Software\ClipNest.Tests\{Guid.NewGuid():N}";
        var executablePath = Path.Combine(
            Path.GetTempPath(),
            "ClipNest Test",
            "ClipNest.exe");
        var service = new AutoStartService(
            executablePath,
            Registry.CurrentUser,
            registryPath,
            "ClipNest");

        try
        {
            Assert.True(service.TrySetEnabled(true, out var enableError), enableError);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
            {
                Assert.NotNull(key);
                Assert.Equal(
                    $"\"{Path.GetFullPath(executablePath)}\"",
                    key.GetValue("ClipNest"));
            }

            Assert.True(service.TrySetEnabled(false, out var disableError), disableError);
            using (var key = Registry.CurrentUser.OpenSubKey(registryPath))
            {
                Assert.NotNull(key);
                Assert.Null(key.GetValue("ClipNest"));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: false);
        }
    }
}
