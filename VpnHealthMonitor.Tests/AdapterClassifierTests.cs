using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-323: which adapters the kill switch blocks direct egress through. The dangerous failures are
/// asymmetric — a missed physical NIC leaves a hole while the UI says "Защищено", a wrongly blocked VPN
/// adapter cuts the user off entirely — so both directions are pinned here.
/// </summary>
public class AdapterClassifierTests
{
    private static NetworkAdapterInfo Adapter(
        string name,
        string description,
        string media,
        bool isVirtual) => new()
    {
        Name = name,
        Description = description,
        PhysicalMediaType = media,
        IsVirtual = isVirtual,
        Status = "Up"
    };

    /// <summary>Real Get-NetAdapter output from the author's machine — the reference the change must not alter.</summary>
    private static IReadOnlyList<NetworkAdapterInfo> AuthorMachine() => new[]
    {
        Adapter("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "Unspecified", true),
        Adapter("Сетевое подключение Bluetooth", "Bluetooth Device (Personal Area Network)", "BlueTooth", true),
        Adapter("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter #2", "Unspecified", true),
        Adapter("Karing TUN Network Adapter", "Karing TUN Network Adapter Tunnel", "Unspecified", true),
        Adapter("Беспроводная сеть", "TP-Link Wireless USB Adapter", "Native 802.11", false),
        Adapter("Подключение по локальной сети", "TAP-Windows Adapter V9", "Unspecified", true),
        Adapter("Ethernet", "Intel(R) I211 Gigabit Network Connection", "802.3", false)
    };

    [Fact]
    public void AuthorMachine_BlocksPhysicalPathsOnly()
    {
        var blocked = AdapterClassifier.SelectBlockable(AuthorMachine()).Select(a => a.Name).ToList();

        Assert.Equal(
            new[] { "Сетевое подключение Bluetooth", "Беспроводная сеть", "Ethernet" }.OrderBy(n => n),
            blocked.OrderBy(n => n));
    }

    [Fact]
    public void AuthorMachine_LeavesVpnAndVirtualAlone()
    {
        var excluded = AdapterClassifier.SelectExcluded(AuthorMachine()).Select(a => a.Name).ToList();

        Assert.Contains("Karing TUN Network Adapter", excluded);
        Assert.Contains("Подключение по локальной сети", excluded);   // TAP-Windows
        Assert.Contains("vEthernet (WSL)", excluded);
        Assert.Contains("vEthernet (Default Switch)", excluded);
    }

    /// <summary>The hole T-316 found: built-in LTE was invisible to the old media-type whitelist.</summary>
    [Theory]
    [InlineData("Сотовая связь", "Intel(R) XMM 7360 LTE Advanced Modem", "Wireless WAN", false)]
    [InlineData("Мобильный модем", "Qualcomm Snapdragon X55 5G Modem", "Wireless WAN", true)]
    [InlineData("Ethernet 2", "USB 10/100 LAN", "802.3", false)]
    [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", "Native 802.11", false)]
    [InlineData("Ethernet 3", "Realtek USB GbE Family Controller", "Unspecified", false)]
    public void PhysicalEgressPaths_AreBlocked(string name, string description, string media, bool isVirtual)
    {
        Assert.True(AdapterClassifier.IsBlockable(Adapter(name, description, media, isVirtual)));
    }

    /// <summary>Blocking any of these would take the protected app offline even with the VPN up.</summary>
    [Theory]
    [InlineData("Mullvad", "Mullvad Tunnel", "Unspecified", true)]
    [InlineData("NordLynx", "NordLynx Tunnel", "Unspecified", true)]
    [InlineData("ProtonVPN TUN", "ProtonVPN TUN Adapter", "Unspecified", true)]
    [InlineData("Ethernet 4", "Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", "802.3", true)]
    [InlineData("Ethernet 5", "Fortinet Virtual Ethernet Adapter (NDIS 6.30)", "802.3", true)]
    [InlineData("CloudflareWARP", "Cloudflare WARP Tunnel", "Unspecified", true)]
    [InlineData("Подключение по локальной сети* 11", "Microsoft Wi-Fi Direct Virtual Adapter #3", "Native 802.11", true)]
    [InlineData("Подключение по локальной сети* 8", "WAN Miniport (Network Monitor)", "Unspecified", true)]
    [InlineData("Teredo Tunneling Pseudo-Interface", "", "Unspecified", true)]
    [InlineData("VMware Network Adapter VMnet1", "VMware Virtual Ethernet Adapter for VMnet1", "802.3", true)]
    public void VpnAndVirtualAdapters_AreNeverBlocked(string name, string description, string media, bool isVirtual)
    {
        Assert.False(AdapterClassifier.IsBlockable(Adapter(name, description, media, isVirtual)));
    }

    [Fact]
    public void VirtualAdapterWithUnspecifiedMedia_IsNotBlocked()
    {
        Assert.False(AdapterClassifier.IsBlockable(Adapter("Что-то своё", "Unknown vendor adapter", "Unspecified", true)));
    }

    [Fact]
    public void AdapterWithoutName_IsIgnored()
    {
        Assert.False(AdapterClassifier.IsBlockable(Adapter(" ", "no name", "802.3", false)));
    }

    [Fact]
    public void EmptyInventory_YieldsEmptyBlockList()
    {
        Assert.Empty(AdapterClassifier.SelectBlockable(Array.Empty<NetworkAdapterInfo>()));
    }
}
