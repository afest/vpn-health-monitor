using Microsoft.Win32;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Запуск вместе с Windows через HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Ветка пользователя, а не машины: манифеста requireAdministrator у приложения нет, права
/// оно просит разово и только под операции с правилами firewall — автозапуск не должен
/// приносить UAC на каждый вход в систему.
/// </summary>
public sealed class AutostartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Имя значения в реестре. Меняя его, оставь миграцию: старое значение иначе зависнет навсегда.</summary>
    internal const string ValueName = "VPN Health Monitor";

    /// <summary>
    /// Путь всегда в кавычках: без них Windows разбирает строку по пробелам, и путь вида
    /// «D:\Programs\VPN Health Monitor\...» превращается в запуск несуществующего «D:\Programs\VPN».
    /// </summary>
    public static string BuildCommand(string executablePath) => $"\"{executablePath.Trim('"')}\"";

    /// <summary>Полный путь к .exe текущего процесса. Для single-file publish это сам .exe, не dll.</summary>
    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;

    /// <summary>Значение в реестре есть и указывает на текущий .exe.</summary>
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(ValueName) as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, BuildCommand(CurrentExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Значение есть, но ведёт на другой .exe — приложение переставили, запись протухла.</summary>
    public bool IsRegisteredForOtherPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        var value = key?.GetValue(ValueName) as string;
        return !string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, BuildCommand(CurrentExecutablePath), StringComparison.OrdinalIgnoreCase);
    }

    public void Enable()
    {
        var exe = CurrentExecutablePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException("Не удалось определить путь к исполняемому файлу.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($@"Не удалось открыть HKCU\{RunKeyPath}.");
        key.SetValue(ValueName, BuildCommand(exe), RegistryValueKind.String);
    }

    /// <summary>Снятие галки удаляет значение целиком, а не пишет пустую строку: пустое значение Windows тоже читает.</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is null)
        {
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public void Apply(bool enabled)
    {
        if (enabled)
        {
            Enable();
        }
        else
        {
            Disable();
        }
    }
}
