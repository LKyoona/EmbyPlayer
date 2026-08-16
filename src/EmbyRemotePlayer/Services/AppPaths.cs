namespace EmbyRemotePlayer.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmbyRemotePlayer");

    public static string CacheDirectory { get; } = Path.Combine(DataDirectory, "cache-v2");
    public static string ImageCacheDirectory { get; } = Path.Combine(CacheDirectory, "images");
    public static string ApiCacheDirectory { get; } = Path.Combine(CacheDirectory, "metadata");
    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");
    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings-v2.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(ImageCacheDirectory);
        Directory.CreateDirectory(ApiCacheDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
