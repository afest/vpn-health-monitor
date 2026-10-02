using System.Diagnostics;
using System.IO;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-473: правило прошлой версии расширения остаётся в Windows, пока старый процесс может работать.
/// Раньше оно показывалось как «Правило вне списка» с кнопкой «Взять под наблюдение», которая
/// молча ничего не делала: вторая запись той же программы склеивалась с первой при сохранении.
/// </summary>
public class PreviousVersionRulesTests
{
    private const string NewClaude =
        @"C:\Users\1\.vscode\extensions\anthropic.claude-code-2.1.287-win32-x64\resources\native-binary\claude.exe";
    private const string OldClaude =
        @"C:\Users\1\.vscode\extensions\anthropic.claude-code-2.1.286-win32-x64\resources\native-binary\claude.exe";
    private const string NewCodex =
        @"C:\Users\1\.vscode\extensions\openai.chatgpt-26.930.21537-win32-x64\bin\windows-x86_64\codex.exe";

    private static FirewallRuleInfo Rule(string program)
        => new(FirewallService.BuildRuleName(program), true, "Block", program);

    private static ProtectedApp Tracked(string path) => new()
    {
        Name = "app",
        Path = path,
        RuleName = FirewallService.BuildRuleName(path),
        IdentityKey = ProtectedAppIdentity.ComputeKey(path)
    };

    [Fact]
    public void Old_extension_rule_is_recognised_as_previous_version_of_tracked_app()
    {
        var apps = new[] { Tracked(NewClaude), Tracked(NewCodex) };
        var untracked = FirewallService.FindUntrackedRules(new[] { Rule(NewClaude), Rule(OldClaude) }, apps);

        var rule = Assert.Single(untracked);
        var newer = FirewallService.FindNewerTrackedVersion(rule, apps);

        Assert.NotNull(newer);
        Assert.Equal(NewClaude, newer!.Path);
    }

    [Fact]
    public void Rule_of_an_unrelated_program_stays_adoptable()
    {
        // Правило, созданное в обход приложения (движ, ручной запуск helper'а), — это не версия
        // ничего из списка: для него «Взять под наблюдение» по-прежнему правильное действие.
        var apps = new[] { Tracked(NewClaude) };

        Assert.Null(FirewallService.FindNewerTrackedVersion(Rule(@"D:\repos\dvizh\claude.exe"), apps));
        Assert.Null(FirewallService.FindNewerTrackedVersion(Rule(NewCodex), apps));
    }

    [Fact]
    public void Rule_without_a_program_is_not_a_previous_version()
    {
        var rule = new FirewallRuleInfo("VPN Health Monitor - Block Direct - x.exe [1]", true, "Block", null);

        Assert.Null(FirewallService.FindNewerTrackedVersion(rule, new[] { Tracked(NewClaude) }));
    }

    // Причина старой ошибки, закреплённая тестом: запись старой версии при сохранении склеивается
    // с записью новой, и в списке остаётся одна. Поэтому кнопка «Взять под наблюдение» для неё
    // и не могла сработать — и поэтому окно теперь её не предлагает.
    [Fact]
    public void Adopting_an_old_version_would_be_merged_away_on_save()
    {
        var settings = new AppSettings { ProtectedApps = new List<ProtectedApp> { Tracked(NewClaude), Tracked(OldClaude) } };

        SettingsService.MigrateProtectedAppIdentity(settings);

        Assert.Single(settings.ProtectedApps);
    }

    [Fact]
    public void Previous_version_statuses_have_their_own_text_glyph_and_hint()
    {
        var glyphs = Enum.GetValues<ProtectionStatus>().Select(status => status.ToGlyph()).ToList();

        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
        Assert.Equal("Старая версия, работает", ProtectionStatus.PreviousVersionInUse.ToDisplayText());
        Assert.Equal("Старая версия, не нужна", ProtectionStatus.PreviousVersionIdle.ToDisplayText());
        Assert.Contains("перезапуска", ProtectionStatus.PreviousVersionInUse.ToHint());
        Assert.Contains("«Удалить»", ProtectionStatus.PreviousVersionIdle.ToHint());
    }

    [Fact]
    public void Row_of_running_previous_version_cannot_be_removed_or_adopted()
    {
        var row = new ProtectedAppRow
        {
            Status = ProtectionStatus.PreviousVersionInUse,
            CanAdopt = false,
            CanReinstall = false,
            CanRemove = false
        };

        Assert.True(row.IsPreviousVersion);
        Assert.True(row.IsPreviousVersionInUse);
        Assert.False(row.CanRemove);
    }

    [Fact]
    public void Probe_sees_the_current_process()
    {
        Assert.True(RunningProcessProbe.CountPossiblyRunningFrom(Environment.ProcessPath) >= 1);
    }

    [Fact]
    public void Probe_finds_nothing_for_a_missing_exe_or_empty_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"vpn-health-monitor-{Guid.NewGuid():N}.exe");

        Assert.Equal(0, RunningProcessProbe.CountPossiblyRunningFrom(missing));
        Assert.Equal(0, RunningProcessProbe.CountPossiblyRunningFrom(null));
        Assert.Equal(0, RunningProcessProbe.CountPossiblyRunningFrom("   "));
    }

    [Fact]
    public void Probe_ignores_a_same_named_exe_from_another_folder()
    {
        // Тот же файл по имени, но в другой папке — процессы текущего .exe сюда не засчитываются.
        var current = Process.GetCurrentProcess().MainModule!.FileName;
        var elsewhere = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), Path.GetFileName(current));

        Assert.Equal(0, RunningProcessProbe.CountPossiblyRunningFrom(elsewhere));
    }
}
