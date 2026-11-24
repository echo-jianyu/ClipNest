namespace ClipNest.Infrastructure;

internal static class AppVersion
{
    public static string Display { get; } = GetDisplay();

    private static string GetDisplay()
    {
        var version = typeof(AppVersion).Assembly.GetName().Version;
        return version is null || version.Major <= 0
            ? "1.0.0"
            : version.ToString(3);
    }
}
