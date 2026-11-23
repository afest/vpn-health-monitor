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
/// <para>
/// То же для приложения Codex: его вспомогательные программы запускает главный процесс ChatGPT.exe,
/// и запущенный до обновления может снова поднять их из старой папки.
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

    /// <summary>
    /// Когда появилась новая версия: время создания её папки (расширения VS Code или версии Codex).
    /// Для остальных программ — null.
    /// </summary>
    internal static DateTime? NewVersionAppearedAt(string? newerExePath)
    {
        string? dir = null;
        if (VsCodeExtensionPathResolver.TryParse(newerExePath, out var root, out var folder, out _, out _))
        {
            dir = Path.Combine(root, folder);
        }
        else if (CodexDesktopPathResolver.GetVersionFolder(newerExePath) is { } codexFolder)
        {
            dir = codexFolder;
        }

        if (dir is null)
        {
            return null;
        }

        try
        {
            return Directory.Exists(dir) ? Directory.GetCreationTime(dir) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Имя главного процесса, который запускает эту программу: VS Code или приложение Codex.</summary>
    internal static string HostProcessName(string extensionPath)
        => CodexDesktopPathResolver.IsVersionedPath(extensionPath)
            ? "ChatGPT"
            : extensionPath.Contains(@"\.vscode-insiders\", StringComparison.OrdinalIgnoreCase) ? "Code - Insiders" : "Code";

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
