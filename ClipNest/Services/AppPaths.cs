using System.IO;

namespace ClipNest.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipNest");

    public static string HistoryFile { get; } = Path.Combine(DataDirectory, "history.json");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");
}
