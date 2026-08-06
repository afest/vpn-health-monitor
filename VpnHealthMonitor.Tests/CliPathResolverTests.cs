using System;
using System.IO;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Pure-function tests for <see cref="CliPathResolver"/> — the path parsing that lets the kill switch follow
/// the Claude Code CLI across its self-update version bumps (sibling of <see cref="AppxPathResolver"/> for
/// MSIX, T-196). The I/O method (ResolveMovedPaths / Directory enumeration) is out of scope per T-182.
/// </summary>
public class CliPathResolverTests
{
    // ---- IsCliVersionedPath --------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\demo\AppData\Roaming\Claude\claude-code\2.1.181\claude.exe", true)]
    [InlineData(@"c:\users\demo\appdata\roaming\claude\claude-code\2.1.181\claude.exe", true)] // case-insensitive
    [InlineData(@"D:\anywhere\claude-code\10.0.0\claude.exe", true)]                          // location-agnostic
    public void IsCliVersionedPath_RealLayout_True(string path, bool expected)
    {
        Assert.Equal(expected, CliPathResolver.IsCliVersionedPath(path));
    }

    [Theory]
    [InlineData(@"C:\Users\demo\AppData\Roaming\Claude\claude-code\2.1.181\node.exe")]   // wrong exe
    [InlineData(@"C:\Users\demo\AppData\Roaming\Claude\claude-code\latest\claude.exe")]  // version dir not numeric
    [InlineData(@"C:\Users\demo\AppData\Roaming\Claude\bin\2.1.181\claude.exe")]         // parent not "claude-code"
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0_x64__abc\app\claude.exe")]  // MSIX, not CLI
    [InlineData(@"C:\tools\claude.exe")]                                              // no version layout
    [InlineData("")]
    [InlineData(null)]
    public void IsCliVersionedPath_NonMatching_False(string? path)
    {
        Assert.False(CliPathResolver.IsCliVersionedPath(path));
    }

    // ---- GetStableKey --------------------------------------------------------------------------

    [Fact]
    public void GetStableKey_VersionedPath_ReturnsLowercasedCodeFolder()
    {
        Assert.Equal(
            @"c:\users\demo\appdata\roaming\claude\claude-code",
            CliPathResolver.GetStableKey(@"C:\Users\demo\AppData\Roaming\Claude\claude-code\2.1.181\claude.exe"));
    }

    [Fact]
    public void GetStableKey_StableAcrossVersionBump()
    {
        var a = CliPathResolver.GetStableKey(@"C:\X\claude-code\2.1.181\claude.exe");
        var b = CliPathResolver.GetStableKey(@"C:\X\claude-code\2.1.182\claude.exe");
        Assert.Equal(a, b);
    }

    [Fact]
    public void GetStableKey_NonCliPath_Null()
    {
        Assert.Null(CliPathResolver.GetStableKey(@"C:\Program Files\Mozilla Firefox\firefox.exe"));
    }

    // ---- IsVersion -----------------------------------------------------------------------------

    [Theory]
    [InlineData("2.1.181", true)]
    [InlineData("1.2", true)]
    [InlineData("2.1.181.5", true)]   // 4 components
    [InlineData("0.0.1", true)]
    [InlineData("2", false)]          // single component
    [InlineData("2.1.181-rc1", false)] // prerelease suffix is non-numeric
    [InlineData("latest", false)]
    [InlineData("2..1", false)]       // empty middle segment
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsVersion_NumericDotted(string? name, bool expected)
    {
        Assert.Equal(expected, CliPathResolver.IsVersion(name));
    }

    // ---- MaxVersionName ------------------------------------------------------------------------

    [Fact]
    public void MaxVersionName_PicksHighest_NotLexicographic()
    {
        // "2.1.9" < "2.1.181" numerically, but lexicographically "2.1.9" sorts last — must not pick it.
        Assert.Equal(
            "2.1.181",
            CliPathResolver.MaxVersionName(new[] { "2.1.9", "2.1.181", "2.1.42" }));
    }

    [Fact]
    public void MaxVersionName_DiffersByMajorMinor()
    {
        Assert.Equal("3.0.0", CliPathResolver.MaxVersionName(new[] { "2.99.99", "3.0.0", "2.100.5" }));
    }

    [Fact]
    public void MaxVersionName_SkipsUnparseable()
    {
        Assert.Equal("1.2.3", CliPathResolver.MaxVersionName(new[] { "latest", "1.2.3", "backup" }));
    }

    [Fact]
    public void MaxVersionName_NoneParse_ReturnsNull()
    {
        Assert.Null(CliPathResolver.MaxVersionName(new[] { "latest", "tmp" }));
    }

    [Fact]
    public void MaxVersionName_Empty_ReturnsNull()
    {
        Assert.Null(CliPathResolver.MaxVersionName(System.Array.Empty<string>()));
    }

    // ---- CompareVersions -----------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { 2, 1, 181 }, new[] { 2, 1, 9 }, 1)]
    [InlineData(new[] { 2, 1, 9 }, new[] { 2, 1, 181 }, -1)]
    [InlineData(new[] { 2, 1, 0 }, new[] { 2, 1 }, 0)]    // zero-padding equality
    [InlineData(new[] { 3 }, new[] { 2, 99, 99 }, 1)]
    public void CompareVersions_ElementWise(int[] a, int[] b, int expectedSign)
    {
        Assert.Equal(expectedSign, System.Math.Sign(CliPathResolver.CompareVersions(a, b)));
    }

    // ---- ResolveMovedPaths (I/O integration) ---------------------------------------------------
    // Deliberately hits the filesystem (unlike the pure tests above). This is the security-critical
    // step: wrong resolution means the kill switch silently re-pins to the wrong/no exe. Self-contained
    // temp dirs, cleaned up in finally.

    [Fact]
    public void ResolveMovedPaths_FollowsToHighestExistingVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-cli-" + Guid.NewGuid().ToString("N"));
        var codeDir = Path.Combine(root, "Claude", "claude-code");
        try
        {
            // Real version folders each with a claude.exe; 1.10.0 is the numeric max (NOT lexicographic).
            foreach (var v in new[] { "1.2.0", "1.10.0", "1.9.0" })
            {
                Directory.CreateDirectory(Path.Combine(codeDir, v));
                File.WriteAllText(Path.Combine(codeDir, v, "claude.exe"), "stub");
            }

            // A higher-sorting version folder WITHOUT the exe must be ignored, not chosen.
            Directory.CreateDirectory(Path.Combine(codeDir, "2.0.0"));

            var oldMissing = Path.Combine(codeDir, "1.0.0", "claude.exe"); // never created → "moved"

            var moved = CliPathResolver.ResolveMovedPaths(new[] { oldMissing });

            Assert.True(moved.ContainsKey(oldMissing));
            Assert.Equal(Path.Combine(codeDir, "1.10.0", "claude.exe"), moved[oldMissing]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ResolveMovedPaths_NoUsableVersionFolders_ReturnsEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-cli-" + Guid.NewGuid().ToString("N"));
        var codeDir = Path.Combine(root, "Claude", "claude-code");
        try
        {
            Directory.CreateDirectory(codeDir); // exists but empty → nothing to follow to
            var oldMissing = Path.Combine(codeDir, "1.0.0", "claude.exe");

            var moved = CliPathResolver.ResolveMovedPaths(new[] { oldMissing });

            Assert.Empty(moved);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
