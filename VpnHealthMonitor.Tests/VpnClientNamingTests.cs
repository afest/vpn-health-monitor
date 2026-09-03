using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

public class VpnClientNamingTests
{
    [Theory]
    // Реальные адаптеры с машины, где ловился T-403.
    [InlineData("hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)", "hidemy.name")]
    [InlineData("Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)", "Karing")]
    // Типовые имена других клиентов.
    [InlineData("ProtonVPN TUN", "ProtonVPN")]
    [InlineData("Mullvad WireGuard Tunnel", "Mullvad")]
    [InlineData("NordLynx Tunnel", "NordLynx")]
    [InlineData("Cisco AnyConnect Secure Mobility Client Virtual Miniport Adapter", "Cisco AnyConnect")]
    public void Extracts_service_name_from_adapter(string adapter, string expected)
        => Assert.Equal(expected, VpnClientNaming.FromAdapterName(adapter));

    [Theory]
    // Generic-адаптеры: имени сервиса в них нет, выдумывать нечего.
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("WireGuard Tunnel")]
    [InlineData("VPN")]
    [InlineData("")]
    [InlineData(null)]
    public void Returns_null_when_nothing_meaningful_left(string? adapter)
        => Assert.Null(VpnClientNaming.FromAdapterName(adapter));

    [Fact]
    public void DescribeExit_links_provider_to_the_vpn_the_user_knows()
    {
        var text = VpnClientNaming.DescribeExit(
            "LeaseWeb Netherlands B.V. (AS60781)",
            "hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)");

        Assert.Contains("LeaseWeb Netherlands B.V. (AS60781)", text);
        Assert.Contains("hidemy.name", text);
    }

    [Fact]
    public void DescribeExit_falls_back_to_provider_alone()
    {
        var text = VpnClientNaming.DescribeExit("LeaseWeb Netherlands B.V. (AS60781)", "TAP-Windows Adapter V9");
        Assert.Equal("LeaseWeb Netherlands B.V. (AS60781)", text);
    }
}
