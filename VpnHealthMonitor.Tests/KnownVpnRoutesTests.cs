using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

public class KnownVpnRoutesTests
{
    private static AppSettings TrustedRoutes() => new()
    {
        RouteMode = VpnRouteMode.SeparateAdapter,
        TreatDefaultRouteChangeAsLeakRisk = true,
        TreatUnexpectedIPv4AsLeakRisk = true,
        ExpectedPublicIPv4 = new() { "203.0.113.7" },
        AllowIpChangesWithinExpectedCountry = false,
        ExpectedCountry = "Германия, Нидерланды, Франция, Казахстан",
        TreatCountryMismatchAsLeakRisk = true,
        TreatProviderChangeAsLeakRisk = true,
        AllowedVpnInterfaces = new()
        {
            "hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)",
            "happ-xray (Happ Tunnel)",
            "happ-tun (sing-tun Tunnel)",
            "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)"
        },
        AllowedProviders = new()
        {
            new() { Asn = "AS216416" },
            new() { Asn = "AS24940" },
            new() { Asn = "AS60068" },
            new() { Asn = "AS199524" }
        }
    };

    private static MonitorStatus Evaluate(string iface, string asn, string country)
    {
        var rolling = new RollingHealthWindow(10);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 30, PacketLossPercent = 0 });
        return HealthEvaluator.Evaluate(new NetworkSnapshot
        {
            ExternalIPv4 = "198.51.100.10",
            Country = country,
            InterfaceName = iface,
            Asn = asn,
            HttpAvailable = true
        }, rolling, TrustedRoutes()).Status;
    }

    [Theory]
    [InlineData("hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)", "AS216416", "DE")]
    [InlineData("happ-xray (Happ Tunnel)", "AS24940", "DE")]
    [InlineData("happ-tun (sing-tun Tunnel)", "AS199524", "KZ")]
    [InlineData("Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)", "AS60068", "NL")]
    public void KnownVpnRoutes_AreAccepted(string iface, string asn, string country)
        => Assert.Equal(MonitorStatus.Ok, Evaluate(iface, asn, country));

    [Theory]
    [InlineData("Беспроводная сеть (TP-Link Wireless USB Adapter)", "AS24940", "DE")]
    [InlineData("hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)", "AS12389", "DE")]
    [InlineData("Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)", "AS60068", "RU")]
    [InlineData("Unknown", "AS216416", "DE")]
    [InlineData("hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)", "", "DE")]
    public void DirectRouteUnknownProviderOrCountry_RemainsRisk(string iface, string asn, string country)
        => Assert.Equal(MonitorStatus.LeakRisk, Evaluate(iface, asn, country));

    [Fact]
    public void LegacySingleAdapter_IsOverriddenByExplicitKnownList()
    {
        var settings = TrustedRoutes();
        settings.ExpectedInterfaceName = "hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)";
        var available = new RouteCheckContext(true, new[] { "Karing TUN Network Adapter" });

        Assert.Equal(RouteCheckState.Active, HealthEvaluator.GetRouteCheckState(settings, available));
    }

    [Fact]
    public void NewIpOnKnownVpn_DoesNotClaimStrictIpCheckIsDisabled()
    {
        var rolling = new RollingHealthWindow(10);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 30, PacketLossPercent = 0 });
        var result = HealthEvaluator.Evaluate(new NetworkSnapshot
        {
            ExternalIPv4 = "198.51.100.10",
            Country = "DE",
            InterfaceName = "hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)",
            Asn = "AS216416",
            HttpAvailable = true
        }, rolling, TrustedRoutes());

        Assert.Equal(MonitorStatus.Ok, result.Status);
        Assert.Contains("подтверждён", result.Description);
        Assert.DoesNotContain("выключен", result.Description);
    }
}
