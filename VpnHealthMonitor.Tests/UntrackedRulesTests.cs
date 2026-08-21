using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Список на экране и настоящий firewall не должны расходиться молча. Правило, созданное в обход
/// приложения (другой сборкой, ручным запуском helper'а, восстановлением с другой машины), обязано
/// быть видно — защита от него реальная, а приложение о ней не знает.
/// </summary>
public class UntrackedRulesTests
{
    private static FirewallRuleInfo Rule(string name, string program)
        => new(name, true, "Block", program);

    private static ProtectedApp App(string ruleName, string path)
        => new() { Name = "app", Path = path, RuleName = ruleName };

    [Fact]
    public void RuleWithoutAnEntry_IsReportedAsUntracked()
    {
        var rules = new[]
        {
            Rule("VPN Health Monitor - Block Direct - claude.exe [479a1bb0]", @"C:\vscode\claude.exe"),
            Rule("VPN Health Monitor - Block Direct - claude.exe [81f61c32]", @"D:\repos\dvizh\claude.exe")
        };
        var apps = new[] { App("VPN Health Monitor - Block Direct - claude.exe [479a1bb0]", @"C:\vscode\claude.exe") };

        var untracked = FirewallService.FindUntrackedRules(rules, apps);

        var one = Assert.Single(untracked);
        Assert.Equal(@"D:\repos\dvizh\claude.exe", one.Program);
    }

    [Fact]
    public void EverythingClaimed_ProducesNothing()
    {
        var rules = new[] { Rule("VPN Health Monitor - Block Direct - a.exe [1]", @"C:\a.exe") };
        var apps = new[] { App("VPN Health Monitor - Block Direct - a.exe [1]", @"C:\a.exe") };

        Assert.Empty(FirewallService.FindUntrackedRules(rules, apps));
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var rules = new[] { Rule("VPN Health Monitor - Block Direct - A.exe [AB12]", @"C:\a.exe") };
        var apps = new[] { App("vpn health monitor - block direct - a.exe [ab12]", @"C:\a.exe") };

        Assert.Empty(FirewallService.FindUntrackedRules(rules, apps));
    }

    [Fact]
    public void EmptyList_MakesEveryRuleUntracked()
    {
        var rules = new[]
        {
            Rule("VPN Health Monitor - Block Direct - a.exe [1]", @"C:\a.exe"),
            Rule("VPN Health Monitor - Block Direct - b.exe [2]", @"C:\b.exe")
        };

        Assert.Equal(2, FirewallService.FindUntrackedRules(rules, Array.Empty<ProtectedApp>()).Count);
    }

    [Fact]
    public void EntryWithoutARuleName_DoesNotSwallowRules()
    {
        // Пустое RuleName не должно случайно «застолбить» чужое правило.
        var rules = new[] { Rule("VPN Health Monitor - Block Direct - a.exe [1]", @"C:\a.exe") };
        var apps = new[] { new ProtectedApp { Name = "битая запись", Path = @"C:\a.exe", RuleName = "" } };

        Assert.Single(FirewallService.FindUntrackedRules(rules, apps));
    }

    [Fact]
    public void UntrackedStatus_HasItsOwnTextAndGlyph()
    {
        // Цвет не единственное отличие: знак читается и на обесцвеченном скриншоте.
        Assert.Equal("Правило вне списка", ProtectionStatus.Untracked.ToDisplayText());
        Assert.NotEqual(ProtectionStatus.Protected.ToGlyph(), ProtectionStatus.Untracked.ToGlyph());
        Assert.NotEqual(ProtectionStatus.Error.ToGlyph(), ProtectionStatus.Untracked.ToGlyph());
        Assert.Contains("под наблюдение", ProtectionStatus.Untracked.ToHint());
    }
}
