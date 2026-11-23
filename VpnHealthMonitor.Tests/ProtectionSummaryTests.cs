using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Блок «Приложения» в плашке статуса и автовосстановление защиты. Контракт: любая программа,
/// прямой выход которой не закрыт, делает блок красным; сам чинится только СЛЕТЕВШИЙ kill switch —
/// правило, снятое человеком, приложение обратно не ставит.
/// </summary>
public class ProtectionSummaryTests
{
    [Fact]
    public void AllProtected_IsNotRed()
    {
        var summary = ProtectionSummary.From(new[] { ProtectionStatus.Protected, ProtectionStatus.Protected });

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Protected);
        Assert.False(summary.HasUnprotected);
    }

    [Theory]
    [InlineData(ProtectionStatus.PathChanged)]
    [InlineData(ProtectionStatus.RulesNotApplied)]
    [InlineData(ProtectionStatus.Error)]
    public void OneDropped_MakesBlockRed(ProtectionStatus dropped)
    {
        var summary = ProtectionSummary.From(new[] { ProtectionStatus.Protected, dropped, ProtectionStatus.Protected });

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.Protected);
        Assert.Equal(1, summary.Unprotected);
        Assert.True(summary.HasUnprotected);
    }

    [Fact]
    public void MissingFile_IsCountedApart_AndIsNotRed()
    {
        var summary = ProtectionSummary.From(new[] { ProtectionStatus.Protected, ProtectionStatus.FileNotFound });

        Assert.Equal(1, summary.Missing);
        Assert.False(summary.HasUnprotected);
    }

    [Fact]
    public void NewVersion_IsRepairedByUpdatingPath()
    {
        Assert.Equal(AutoRepairKind.UpdatePath,
            ProtectionAutoRepair.Decide(ProtectionStatus.PathChanged, canVerifyLive: false, wasApplied: true, hasNewPath: true));
    }

    [Fact]
    public void NewVersion_WithoutResolvedPath_IsLeftToHuman()
    {
        Assert.Equal(AutoRepairKind.None,
            ProtectionAutoRepair.Decide(ProtectionStatus.PathChanged, canVerifyLive: true, wasApplied: true, hasNewPath: false));
    }

    [Theory]
    [InlineData(ProtectionStatus.RulesNotApplied)]
    [InlineData(ProtectionStatus.Error)]
    public void LostRule_IsReapplied_WhenItWasAppliedBefore(ProtectionStatus status)
    {
        Assert.Equal(AutoRepairKind.Reapply,
            ProtectionAutoRepair.Decide(status, canVerifyLive: true, wasApplied: true, hasNewPath: false));
    }

    [Fact]
    public void RuleNeverApplied_OrDisabledByHuman_IsNotTouched()
    {
        // «Отключить защиту» стирает дату применения: вернуть правило без спроса — спорить с человеком.
        Assert.Equal(AutoRepairKind.None,
            ProtectionAutoRepair.Decide(ProtectionStatus.RulesNotApplied, canVerifyLive: true, wasApplied: false, hasNewPath: false));
    }

    [Fact]
    public void WithoutLiveFirewallRead_LostRuleIsNotGuessed()
    {
        Assert.Equal(AutoRepairKind.None,
            ProtectionAutoRepair.Decide(ProtectionStatus.RulesNotApplied, canVerifyLive: false, wasApplied: true, hasNewPath: false));
    }

    [Theory]
    [InlineData(ProtectionStatus.Protected)]
    [InlineData(ProtectionStatus.FileNotFound)]
    [InlineData(ProtectionStatus.Untracked)]
    [InlineData(ProtectionStatus.PreviousVersionInUse)]
    [InlineData(ProtectionStatus.PreviousVersionIdle)]
    public void OtherStatuses_AreNotRepaired(ProtectionStatus status)
    {
        Assert.Equal(AutoRepairKind.None,
            ProtectionAutoRepair.Decide(status, canVerifyLive: true, wasApplied: true, hasNewPath: true));
    }
}
