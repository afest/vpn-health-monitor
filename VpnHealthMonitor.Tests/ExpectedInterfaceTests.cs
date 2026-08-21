using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// The expected VPN adapter is stored as alias + description, not as one "Alias (Description)" blob.
/// These cover the split, the migration of files written before it, and matching that survives a
/// driver-description change.
/// </summary>
public class ExpectedInterfaceTests
{
    [Theory]
    [InlineData("Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)",
        "Karing TUN Network Adapter", "Karing TUN Network Adapter Tunnel")]
    [InlineData("Беспроводная сеть (TP-Link Wireless USB Adapter)",
        "Беспроводная сеть", "TP-Link Wireless USB Adapter")]
    [InlineData("Acme Secure Link", "Acme Secure Link", "")]
    [InlineData("", "", "")]
    [InlineData("Unknown", "", "")]
    public void SplitDisplay_SeparatesAliasFromDescription(string display, string alias, string description)
    {
        var parsed = ExpectedInterface.SplitDisplay(display);

        Assert.Equal(alias, parsed.Alias);
        Assert.Equal(description, parsed.Description);
    }

    [Fact]
    public void SplitDisplay_KeepsNameWhole_WhenTheOnlyBracketsAreLeading()
    {
        // "(Wi-Fi) adapter" has no trailing group: treating any bracket as a description would
        // shred a legitimate adapter name.
        var parsed = ExpectedInterface.SplitDisplay("(Wi-Fi) adapter");

        Assert.Equal("(Wi-Fi) adapter", parsed.Alias);
        Assert.Equal(string.Empty, parsed.Description);
    }

    [Fact]
    public void FromSettings_MigratesLegacyCompositeString()
    {
        var settings = new AppSettings
        {
            ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)"
        };

        var identity = ExpectedInterface.FromSettings(settings);

        Assert.Equal("Karing TUN Network Adapter", identity.Alias);
        Assert.Equal("Karing TUN Network Adapter Tunnel", identity.Description);
    }

    [Fact]
    public void FromSettings_FallsBackToBaseline_WhenNothingElseIsSaved()
    {
        var settings = new AppSettings
        {
            Baseline = new BaselineInfo { InterfaceName = "Acme Link (Acme Tunnel Driver)" }
        };

        var identity = ExpectedInterface.FromSettings(settings);

        Assert.Equal("Acme Link", identity.Alias);
        Assert.Equal("Acme Tunnel Driver", identity.Description);
    }

    [Fact]
    public void FromSettings_IsEmpty_InNoSeparateAdapterMode()
    {
        var settings = new AppSettings
        {
            RouteMode = VpnRouteMode.NoSeparateAdapter,
            ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)"
        };

        Assert.True(ExpectedInterface.FromSettings(settings).IsEmpty);
    }

    [Fact]
    public void Matches_OnAlias_WhenDriverDescriptionChanged()
    {
        var identity = new ExpectedInterfaceIdentity(
            "Karing TUN Network Adapter", "Karing TUN Network Adapter Tunnel", string.Empty);

        Assert.True(identity.Matches("Karing TUN Network Adapter", "Karing TUN Adapter v2"));
    }

    [Fact]
    public void Matches_OnDescription_WhenUserRenamedTheConnection()
    {
        var identity = new ExpectedInterfaceIdentity(
            "Karing TUN Network Adapter", "Karing TUN Network Adapter Tunnel", string.Empty);

        Assert.True(identity.Matches("VPN", "Karing TUN Network Adapter Tunnel"));
    }

    [Fact]
    public void Matches_IsFalse_ForAnUnrelatedAdapter()
    {
        var identity = new ExpectedInterfaceIdentity(
            "Karing TUN Network Adapter", "Karing TUN Network Adapter Tunnel", string.Empty);

        Assert.False(identity.Matches("Беспроводная сеть", "TP-Link Wireless USB Adapter"));
    }

    [Fact]
    public void Matches_DoesNotFalsePositive_OnSubstringOverlap()
    {
        // The old two-way substring test matched "Ethernet" against "Ethernet 2" and vice versa.
        var identity = new ExpectedInterfaceIdentity("Ethernet", string.Empty, string.Empty);

        Assert.False(identity.Matches("Ethernet 2", "Realtek Gaming 2.5GbE Family Controller"));
    }

    [Fact]
    public void Matches_OnGuid_EvenWhenBothNamesChanged()
    {
        var identity = new ExpectedInterfaceIdentity("Old Name", "Old Description", "{4C9FC25E-1767-73BE-48B5-B5C7C6B81384}");

        Assert.True(identity.Matches("New Name", "New Description", "{4C9FC25E-1767-73BE-48B5-B5C7C6B81384}"));
    }

    [Fact]
    public void MatchesDisplay_TreatsUnknownAsNoEvidence()
    {
        var identity = new ExpectedInterfaceIdentity("Karing TUN Network Adapter", string.Empty, string.Empty);

        Assert.True(ExpectedInterface.MatchesDisplay(identity, "Unknown"));
        Assert.True(ExpectedInterface.MatchesDisplay(identity, null));
    }

    [Fact]
    public void BuildDisplay_CollapsesDuplicateHalves()
    {
        Assert.Equal("Acme Link", ExpectedInterface.BuildDisplay("Acme Link", "Acme Link"));
        Assert.Equal("Acme Link", ExpectedInterface.BuildDisplay("Acme Link", ""));
        Assert.Equal("Acme Link (Driver)", ExpectedInterface.BuildDisplay("Acme Link", "Driver"));
    }
}
