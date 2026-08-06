using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-323: the "VPN route" check must admit when it cannot fire. In system-proxy mode the default route
/// never leaves Wi-Fi, so comparing it with a non-VPN expected interface is a check that stays green
/// whatever happens to the tunnel.
/// </summary>
public class RouteCheckStateTests
{
    [Fact]
    public void Disabled_WhenRouteRiskIsOff()
    {
        var settings = new AppSettings
        {
            TreatDefaultRouteChangeAsLeakRisk = false,
            ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)"
        };

        Assert.Equal(RouteCheckState.Disabled, HealthEvaluator.GetRouteCheckState(settings));
    }

    [Fact]
    public void Active_WhenExpectedInterfaceLooksLikeVpn()
    {
        var settings = new AppSettings
        {
            TreatDefaultRouteChangeAsLeakRisk = true,
            ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)"
        };

        Assert.Equal(RouteCheckState.Active, HealthEvaluator.GetRouteCheckState(settings));
    }

    [Fact]
    public void NotApplicable_WhenExpectedInterfaceIsPhysical()
    {
        // Proxy-mode user pressed "baseline": the expected interface got filled with plain Wi-Fi.
        var settings = new AppSettings
        {
            TreatDefaultRouteChangeAsLeakRisk = true,
            ExpectedInterfaceName = "Беспроводная сеть (TP-Link Wireless USB Adapter)"
        };

        Assert.Equal(RouteCheckState.NotApplicable, HealthEvaluator.GetRouteCheckState(settings));
    }

    [Fact]
    public void NotApplicable_WhenExpectedInterfaceIsEmpty()
    {
        var settings = new AppSettings { TreatDefaultRouteChangeAsLeakRisk = true };

        Assert.Equal(RouteCheckState.NotApplicable, HealthEvaluator.GetRouteCheckState(settings));
    }

    [Fact]
    public void Active_WhenOnlyBaselineCarriesTheVpnInterface()
    {
        var settings = new AppSettings
        {
            TreatDefaultRouteChangeAsLeakRisk = true,
            Baseline = new BaselineInfo { InterfaceName = "wg0 (WireGuard Tunnel)" }
        };

        Assert.Equal(RouteCheckState.Active, HealthEvaluator.GetRouteCheckState(settings));
    }

    [Fact]
    public void EvaluateCarriesTheStateIntoTheResult()
    {
        var settings = new AppSettings
        {
            TreatDefaultRouteChangeAsLeakRisk = true,
            ExpectedInterfaceName = "Беспроводная сеть (TP-Link Wireless USB Adapter)"
        };

        var snapshot = new NetworkSnapshot
        {
            CheckedAt = DateTimeOffset.Now,
            ExternalIPv4 = "203.0.113.10",
            Country = "SE",
            InterfaceName = "Беспроводная сеть (TP-Link Wireless USB Adapter)",
            HttpAvailable = true,
            PingSuccesses = 2,
            PingAttempts = 2
        };

        var result = HealthEvaluator.Evaluate(snapshot, new RollingHealthWindow(20), settings);

        Assert.Equal(RouteCheckState.NotApplicable, result.RouteCheck);
    }
}
