using System.Diagnostics;
using System.IO;

namespace VpnHealthMonitor.Services;

/// <summary>Почему прошлая версия считается занятой — от этого зависит, что сказать человеку.</summary>
public sealed record PreviousVersionVerdict(bool InUse, int RunningFromOldExe, bool HostStartedBeforeUpdate);

/// <summary>
/// Занята ли ещё прошлая версия программы из списка: можно ли снять её правило, не выпустив
/// ничего мимо VPN (T-473 / T-482).
/// <para>
/// Мало проверить, что из старого .exe ничего не запущено. Окно VS Code, открытое до обновления
/// расширения, работает на старой версии, даже если её процесс сейчас закрыт: открой панель
/// Claude Code — и он стартует из старой папки, уже без правила. Поэтому для расширений VS Code
/// версия занята, пока жив хоть один процесс VS Code, запущенный раньше, чем появилась новая
/// версия. «Reload Window» главный процесс не перезапускает — нужен полный перезапуск.
/// </para>
/// </summary>
public static class PreviousVersionUsage
{
    public static PreviousVersionVerdict Check(string oldExePath, string? newerExePath)
    {
        var running = RunningProcessProbe.CountPossiblyRunningFrom(oldExePath);
        var appearedAt = NewVersionAppearedAt(newerExePath);
        var hostStarts = appearedAt is null || newerExePath is null
            ? Array.Empty<DateTime?>()
            : HostStartTimes(newerExePath);
        return Evaluate(running, appearedAt, hostStarts);
    }

    /// <param name="hostStartTimes">Время старта процессов VS Code; null — прочитать не удалось.</param>
    public static PreviousVersionVerdict Evaluate(
        int runningFromOldExe,
        DateTime? newVersionAppearedAt,
        IEnumerable<DateTime?> hostStartTimes)
    {
        // Процесс, время старта которого не прочиталось, считается старым: при сомнении правило остаётся.
        var hostOld = newVersionAppearedAt is { } appeared
            && hostStartTimes.Any(start => start is null || start.Value < appeared);
        return new PreviousVersionVerdict(runningFromOldExe > 0 || hostOld, runningFromOldExe, hostOld);
    }

    /// <summary>Когда VS Code скачал новую версию расширения: время создания её папки. Не расширение — null.</summary>
    internal static DateTime? NewVersionAppearedAt(string? newerExePath)
    {
        if (!VsCodeExtensionPathResolver.TryParse(newerExePath, out var root, out var folder, out _, out _))
        {
            return null;
        }

        try
        {
            var dir = Path.Combine(root, folder);
            return Directory.Exists(dir) ? Directory.GetCreationTime(dir) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Имя процесса VS Code, которому принадлежит папка расширений.</summary>
    internal static string HostProcessName(string extensionPath)
        => extensionPath.Contains(@"\.vscode-insiders\", StringComparison.OrdinalIgnoreCase) ? "Code - Insiders" : "Code";

    private static IReadOnlyList<DateTime?> HostStartTimes(string extensionPath)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(HostProcessName(extensionPath));
        }
        catch
        {
            return Array.Empty<DateTime?>();
        }

        var result = new List<DateTime?>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    result.Add(process.StartTime);
                }
                catch
                {
                    result.Add(null);
                }
            }
        }

        return result;
    }
}
