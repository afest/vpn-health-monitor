using System.IO;

namespace VpnHealthMonitor.Services;

public static class AppPaths
{
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnHealthMonitor");

    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public static string DefaultLogsFolder => Path.Combine(AppDataFolder, "logs");

    public static void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(DefaultLogsFolder);
    }
}
