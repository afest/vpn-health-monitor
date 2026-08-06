using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Pure-function tests for <see cref="AppxPathResolver"/> — the path parsing that the kill switch
/// relies on to follow Store/MSIX apps across version bumps (T-196). I/O methods
/// (ResolveMovedPathsAsync / Get-AppxPackage) are out of scope per T-182.
/// </summary>
public class AppxPathResolverTests
{
    // ---- GetFamilyName ------------------------------------------------------------------------

    [Fact]
    public void GetFamilyName_RealClaudePackage_DropsVersionAndArch()
    {
        // The exact case from the doc-comment / acceptance.
        Assert.Equal(
            "Claude_pzs8sxrjxfjjc",
            AppxPathResolver.GetFamilyName("Claude_1.11187.4.0_x64__pzs8sxrjxfjjc"));
    }

    [Fact]
    public void GetFamilyName_AlreadyFamilyName_RoundTrips()
    {
        // {Name}_{PublisherId} with no version/arch → first + last segment is unchanged.
        Assert.Equal(
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            AppxPathResolver.GetFamilyName("Microsoft.WindowsCalculator_8wekyb3d8bbwe"));
    }

    [Fact]
    public void GetFamilyName_NameContainsUnderscore_IsMisderived_DocumentedLimitation()
    {
        // KNOWN edge case (acceptance): the heuristic assumes a package Name never contains '_'.
        // For "My_App_1.0_x64__pub" the true family is "My_App_pub", but first+last segment yields
        // "My_pub". This test pins the CURRENT (wrong-for-this-input) behaviour as golden master —
        // see executor notes. Do not "fix" it here; that is a separate decision.
        Assert.Equal("My_pub", AppxPathResolver.GetFamilyName("My_App_1.0_x64__pub"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Claude")] // single segment, < 2 parts
    public void GetFamilyName_InvalidInput_ReturnsNull(string folder)
    {
        Assert.Null(AppxPathResolver.GetFamilyName(folder));
    }

    // ---- TryParse -----------------------------------------------------------------------------

    [Fact]
    public void TryParse_PackagedExePath_SplitsFolderAndRelativeExe()
    {
        var ok = AppxPathResolver.TryParse(
            @"C:\Program Files\WindowsApps\Claude_1.11187.4.0_x64__pzs8sxrjxfjjc\app\claude.exe",
            out var folder,
            out var relativeExePath);

        Assert.True(ok);
        Assert.Equal("Claude_1.11187.4.0_x64__pzs8sxrjxfjjc", folder);
        Assert.Equal(@"app\claude.exe", relativeExePath);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Vendor\app.exe")]                       // not under WindowsApps
    [InlineData(@"C:\Program Files\WindowsApps\OnlyFolderNoExe")]          // no in-package slash
    [InlineData(@"C:\Program Files\WindowsApps\Folder\")]                  // slash is the last char
    [InlineData("")]                                                       // empty
    public void TryParse_NonPackagedOrMalformed_ReturnsFalse(string path)
    {
        var ok = AppxPathResolver.TryParse(path, out var folder, out var relativeExePath);

        Assert.False(ok);
        Assert.Equal(string.Empty, folder);
        Assert.Equal(string.Empty, relativeExePath);
    }

    // ---- IsPackagedPath -----------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Pkg_1.0_x64__abc\app.exe", true)]
    [InlineData(@"c:\program files\windowsapps\pkg\app.exe", true)] // case-insensitive marker
    [InlineData(@"C:\Program Files\Vendor\app.exe", false)]
    [InlineData("", false)]
    public void IsPackagedPath_DetectsWindowsAppsMarker(string path, bool expected)
    {
        Assert.Equal(expected, AppxPathResolver.IsPackagedPath(path));
    }
}
