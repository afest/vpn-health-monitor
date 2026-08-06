using System.Text.RegularExpressions;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Pure-function tests for <see cref="FirewallService"/>. Only the deterministic helpers
/// (BuildRuleName, PathsMatch) are covered; the elevated PowerShell I/O (Apply/Remove/Query)
/// is out of scope per T-182. PathsMatch is internal — reachable via InternalsVisibleTo.
/// </summary>
public class FirewallServiceTests
{
    // ---- BuildRuleName ------------------------------------------------------------------------

    [Fact]
    public void BuildRuleName_HasPrefixFileNameAndHexHash()
    {
        var name = FirewallService.BuildRuleName(@"C:\Apps\claude.exe");

        Assert.StartsWith("VPN Health Monitor - Block Direct - claude.exe [", name);
        Assert.Matches(@"\[[0-9a-f]{8}\]$", name); // 4-byte SHA-256 prefix, lowercase hex
    }

    [Fact]
    public void BuildRuleName_IsDeterministic_ForSamePath()
    {
        Assert.Equal(
            FirewallService.BuildRuleName(@"C:\Apps\claude.exe"),
            FirewallService.BuildRuleName(@"C:\Apps\claude.exe"));
    }

    [Fact]
    public void BuildRuleName_SameFileNameDifferentFolder_DiffersByHash()
    {
        // Hash is computed over the full path, so two equally-named exes in different folders
        // get distinct rule names (no collision).
        var a = FirewallService.BuildRuleName(@"C:\A\app.exe");
        var b = FirewallService.BuildRuleName(@"C:\B\app.exe");

        Assert.NotEqual(a, b);
        Assert.StartsWith("VPN Health Monitor - Block Direct - app.exe [", a);
        Assert.StartsWith("VPN Health Monitor - Block Direct - app.exe [", b);
    }

    [Fact]
    public void BuildRuleName_CaseOnlyPathDifference_SharesHash()
    {
        // Hash input is lower-cased, so case-only differences in the path collapse to one hash.
        var lower = HashPart(FirewallService.BuildRuleName(@"c:\apps\app.exe"));
        var upper = HashPart(FirewallService.BuildRuleName(@"C:\APPS\app.exe"));

        Assert.Equal(lower, upper);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildRuleName_BlankPath_FallsBackToApp(string path)
    {
        var name = FirewallService.BuildRuleName(path);
        Assert.StartsWith("VPN Health Monitor - Block Direct - app [", name);
    }

    // ---- PathsMatch ---------------------------------------------------------------------------

    [Fact]
    public void PathsMatch_IdenticalPaths_True()
    {
        Assert.True(FirewallService.PathsMatch(@"C:\Apps\app.exe", @"C:\Apps\app.exe"));
    }

    [Fact]
    public void PathsMatch_CaseInsensitive_True()
    {
        Assert.True(FirewallService.PathsMatch(@"C:\Apps\App.exe", @"c:\apps\app.exe"));
    }

    [Fact]
    public void PathsMatch_TrailingSeparatorIgnored_True()
    {
        Assert.True(FirewallService.PathsMatch(@"C:\Apps\", @"C:\Apps"));
    }

    [Fact]
    public void PathsMatch_NormalizesRelativeSegments_True()
    {
        // GetFullPath collapses the ".." — a rooted path keeps this deterministic regardless of CWD.
        Assert.True(FirewallService.PathsMatch(@"C:\Apps\..\Apps\app.exe", @"C:\Apps\app.exe"));
    }

    [Fact]
    public void PathsMatch_DifferentPaths_False()
    {
        Assert.False(FirewallService.PathsMatch(@"C:\A\app.exe", @"C:\B\app.exe"));
    }

    [Theory]
    [InlineData(null, @"C:\Apps\app.exe")]
    [InlineData(@"C:\Apps\app.exe", null)]
    [InlineData(null, null)]
    [InlineData("   ", "   ")]
    public void PathsMatch_NullOrBlank_False(string? a, string? b)
    {
        Assert.False(FirewallService.PathsMatch(a, b));
    }

    private static string HashPart(string ruleName) => Regex.Match(ruleName, @"\[[0-9a-f]{8}\]$").Value;
}
