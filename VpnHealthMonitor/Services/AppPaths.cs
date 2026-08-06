using System.IO;

namespace VpnHealthMonitor.Services;

public static class AppPaths
{
    public static string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VpnHealthMonitor");

    public static string SettingsPath => Path.Combine(AppDataFolder, "settings.json");

    public static string DefaultLogsFolder => Path.Combine(AppDataFolder, "logs");

    /// <summary>Marker file: экран первого запуска (T-334) показан. Отдельный файл, а не поле в AppSettings —
    /// первый запуск не связан с настройками мониторинга и не должен проходить через их normalize/reset.</summary>
    public static string FirstRunMarkerPath => Path.Combine(AppDataFolder, "firstrun.ok");

    public static void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(DefaultLogsFolder);
    }
}
