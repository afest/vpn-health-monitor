using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-323/T-391: route protection must distinguish a real adapter, an explicit no-adapter mode,
/// and a saved adapter that disappeared after reinstalling Windows.
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
    public void NotApplicable_WhenNoSeparateAdapterModeIsSelected()
    {
        var settings = new AppSettings
        {
            RouteMode = VpnRouteMode.NoSeparateAdapter,
            TreatDefaultRouteChangeAsLeakRisk = true,
            ExpectedInterfaceName = "Karing TUN Network Adapter"
        };

        Assert.Equal(RouteCheckState.NotApplicable, HealthEvaluator.GetRouteCheckState(settings));
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
    public void NeedsConfiguration_WhenLegacyExpectedInterfaceIsPhysical()
    {
        // Proxy-mode user pressed "baseline": the expected interface got filled with plain Wi-Fi.
        var settings = new AppSettings
        {
            TreatDefaultRouteChangeAsLeakRisk = true,
            ExpectedInterfaceName = "Беспроводная сеть (TP-Link Wireless USB Adapter)"
        };

        Assert.Equal(RouteCheckState.NeedsConfiguration, HealthEvaluator.GetRouteCheckState(settings));
    }

    [Fact]
    public void NeedsConfiguration_WhenExpectedInterfaceIsEmpty()
    {
        var settings = new AppSettings { TreatDefaultRouteChangeAsLeakRisk = true };

        Assert.Equal(RouteCheckState.NeedsConfiguration, HealthEvaluator.GetRouteCheckState(settings));
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

        Assert.Equal(RouteCheckState.NeedsConfiguration, result.RouteCheck);
    }

    [Fact]
    public void NeedsConfiguration_WhenSavedVpnInterfaceIsMissingFromLiveInventory()
    {
        var settings = new AppSettings
        {
            ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)",
            TreatDefaultRouteChangeAsLeakRisk = true
        };
        var context = Context("Беспроводная сеть (TP-Link Wireless USB Adapter)");

        Assert.Equal(RouteCheckState.NeedsConfiguration, HealthEvaluator.GetRouteCheckState(settings, context));
    }

    [Fact]
    public void Active_WhenExplicitlySelectedAdapterExists_EvenWithUnknownVendorName()
    {
        var settings = new AppSettings
        {
            RouteMode = VpnRouteMode.SeparateAdapter,
            ExpectedInterfaceName = "Acme Secure Link",
            TreatDefaultRouteChangeAsLeakRisk = true
        };

        Assert.Equal(
            RouteCheckState.Active,
            HealthEvaluator.GetRouteCheckState(settings, Context("Acme Secure Link")));
    }

    [Fact]
    public void Evaluate_DoesNotReportLeak_WhenSavedAdapterIsStaleButVpnExitMatches()
    {
        var settings = new AppSettings
        {
            ExpectedCountry = "NL",
            TreatCountryMismatchAsLeakRisk = true,
            ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)",
            TreatDefaultRouteChangeAsLeakRisk = true
        };
        var snapshot = Snapshot("NL", "Беспроводная сеть (TP-Link Wireless USB Adapter)");

        var result = HealthEvaluator.Evaluate(snapshot, RollingHealthy(), settings, Context(snapshot.InterfaceName!));

        Assert.Equal(MonitorStatus.ConfigurationRequired, result.Status);
        Assert.Equal(RouteCheckState.NeedsConfiguration, result.RouteCheck);
    }

    [Fact]
    public void Evaluate_StillReportsLeak_WhenConfiguredTunnelRouteMovesToPhysicalAdapter()
    {
        var settings = new AppSettings
        {
            RouteMode = VpnRouteMode.SeparateAdapter,
            ExpectedInterfaceName = "Acme Secure Link",
            TreatDefaultRouteChangeAsLeakRisk = true
        };

        var result = HealthEvaluator.Evaluate(
            Snapshot(null, "Ethernet"),
            RollingHealthy(),
            settings,
            Context("Acme Secure Link", "Ethernet"));

        Assert.Equal(MonitorStatus.LeakRisk, result.Status);
        Assert.Equal(RouteCheckState.Active, result.RouteCheck);
    }

    [Fact]
    public void ExitCheck_NotConfigured_WhenNoAdapterModeHasNoDirectExitSignals()
    {
        var settings = new AppSettings { RouteMode = VpnRouteMode.NoSeparateAdapter };

        Assert.Equal(VpnExitCheckState.NotConfigured, HealthEvaluator.GetExitCheckState(settings));
    }

    [Fact]
    public void Evaluate_NoAdapterModeWithoutExitSignals_RequiresConfigurationInsteadOfShowingGreen()
    {
        var settings = new AppSettings
        {
            RouteMode = VpnRouteMode.NoSeparateAdapter,
            TreatCountryMismatchAsLeakRisk = false,
            TreatProviderChangeAsLeakRisk = false,
            TreatUnexpectedIPv4AsLeakRisk = false
        };

        var result = HealthEvaluator.Evaluate(
            Snapshot("NL", "Беспроводная сеть"),
            RollingHealthy(),
            settings,
            Context("Беспроводная сеть"));

        Assert.Equal(MonitorStatus.ConfigurationRequired, result.Status);
        Assert.Contains("нечем отличить", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExitCheck_Configured_WhenNoAdapterModeUsesCountryOrProvider()
    {
        var byCountry = new AppSettings
        {
            RouteMode = VpnRouteMode.NoSeparateAdapter,
            ExpectedCountry = "NL",
            TreatCountryMismatchAsLeakRisk = true
        };
        var byProvider = new AppSettings
        {
            RouteMode = VpnRouteMode.NoSeparateAdapter,
            TreatCountryMismatchAsLeakRisk = false,
            TreatProviderChangeAsLeakRisk = true,
            AllowedProviders = new List<ProviderIdentity> { new() { Asn = "AS60068" } }
        };

        Assert.Equal(VpnExitCheckState.Configured, HealthEvaluator.GetExitCheckState(byCountry));
        Assert.Equal(VpnExitCheckState.Configured, HealthEvaluator.GetExitCheckState(byProvider));
    }

    private static RouteCheckContext Context(params string[] names) => new(true, names);

    private static NetworkSnapshot Snapshot(string? country, string iface) => new()
    {
        CheckedAt = DateTimeOffset.Now,
        ExternalIPv4 = "203.0.113.10",
        Country = country,
        InterfaceName = iface,
        HttpAvailable = true,
        PingSuccesses = 2,
        PingAttempts = 2
    };

    private static RollingHealthWindow RollingHealthy()
    {
        var rolling = new RollingHealthWindow(20);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 20, PacketLossPercent = 0 });
        return rolling;
    }
}
