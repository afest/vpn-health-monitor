using System.Diagnostics;
using System.IO;
using System.Text;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-482: «не нужна» у старой версии расширения появляется только после полного перезапуска VS Code.
/// Окно, открытое до обновления, может запустить старый sidecar уже без правила, даже если сейчас
/// из старого .exe ничего не запущено.
/// </summary>
public class PreviousVersionUsageTests
{
    private static readonly DateTime NewVersionAt = new(2026, 10, 2, 11, 17, 54);

    [Fact]
    public void Running_old_exe_keeps_the_version_in_use()
    {
        var verdict = PreviousVersionUsage.Evaluate(7, NewVersionAt, new DateTime?[] { NewVersionAt.AddHours(1) });

        Assert.True(verdict.InUse);
        Assert.Equal(7, verdict.RunningFromOldExe);
        Assert.False(verdict.HostStartedBeforeUpdate);
    }

    // Второе окно: старый claude.exe сейчас не запущен, но VS Code жив с утра — до обновления.
    [Fact]
    public void Vs_code_started_before_the_update_keeps_the_version_in_use()
    {
        var verdict = PreviousVersionUsage.Evaluate(0, NewVersionAt,
            new DateTime?[] { NewVersionAt.AddHours(-3), NewVersionAt.AddMinutes(10) });

        Assert.True(verdict.InUse);
        Assert.True(verdict.HostStartedBeforeUpdate);
    }

    [Fact]
    public void Full_restart_of_vs_code_frees_the_old_version()
    {
        var verdict = PreviousVersionUsage.Evaluate(0, NewVersionAt,
            new DateTime?[] { NewVersionAt.AddMinutes(30), NewVersionAt.AddMinutes(31) });

        Assert.False(verdict.InUse);
    }

    [Fact]
    public void Vs_code_not_running_frees_the_old_version()
    {
        Assert.False(PreviousVersionUsage.Evaluate(0, NewVersionAt, Array.Empty<DateTime?>()).InUse);
    }

    [Fact]
    public void Unreadable_start_time_counts_as_old()
    {
        Assert.True(PreviousVersionUsage.Evaluate(0, NewVersionAt, new DateTime?[] { null }).InUse);
    }

    // Не расширение (CLI, MSIX): решают только процессы из старого .exe.
    [Fact]
    public void Without_an_extension_folder_only_processes_matter()
    {
        Assert.False(PreviousVersionUsage.Evaluate(0, null, new DateTime?[] { NewVersionAt.AddHours(-3) }).InUse);
        Assert.True(PreviousVersionUsage.Evaluate(1, null, Array.Empty<DateTime?>()).InUse);
    }

    [Fact]
    public void New_version_time_is_the_creation_time_of_its_extension_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, ".vscode", "extensions", "anthropic.claude-code-2.1.287-win32-x64");
        var exe = Path.Combine(folder, "resources", "native-binary", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        try
        {
            var appeared = PreviousVersionUsage.NewVersionAppearedAt(exe);

            Assert.NotNull(appeared);
            Assert.Equal(Directory.GetCreationTime(folder), appeared!.Value);
            Assert.Null(PreviousVersionUsage.NewVersionAppearedAt(@"C:\Programs\Tool\tool.exe"));
            Assert.Null(PreviousVersionUsage.NewVersionAppearedAt(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Host_process_follows_the_extensions_folder()
    {
        Assert.Equal("Code",
            PreviousVersionUsage.HostProcessName(@"C:\Users\1\.vscode\extensions\a.b-1.0.0\x.exe"));
        Assert.Equal("Code - Insiders",
            PreviousVersionUsage.HostProcessName(@"C:\Users\1\.vscode-insiders\extensions\a.b-1.0.0\x.exe"));
    }

    // Скрипт с правами администратора правился (уборка старых правил при «Обновить путь»), а запустить
    // его в тесте нельзя — нужен UAC. Разбираем его парсером PowerShell 5.1 из файла, записанного так
    // же, как это делает приложение: UTF-8 без BOM. Синтаксическая ошибка уронила бы и обновление пути.
    [Fact]
    public void Elevated_helper_script_parses_in_windows_powershell()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vhm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "killswitch-helper.ps1");
        File.WriteAllText(script, FirewallService.HelperScript, new UTF8Encoding(false));
        try
        {
            var check = "$e = $null; $t = $null; "
                + $"[void][System.Management.Automation.Language.Parser]::ParseFile('{script}', [ref]$t, [ref]$e); "
                + "if ($e.Count -gt 0) { $e | ForEach-Object { $_.Message }; exit 1 } else { 'OK' }";
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(check);

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, output);
            Assert.Contains("staleRules", FirewallService.HelperScript);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
