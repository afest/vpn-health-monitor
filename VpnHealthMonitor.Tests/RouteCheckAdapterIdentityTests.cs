using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Route check against a real adapter inventory. Covers the live regression: the saved Karing adapter
/// was present in Windows the whole time, yet the app reported "НУЖНА НАСТРОЙКА".
/// </summary>
public class RouteCheckAdapterIdentityTests
{
    private static NetworkAdapterInfo Karing() => new()
    {
        Name = "Karing TUN Network Adapter",
        Description = "Karing TUN Network Adapter Tunnel",
        Status = "Up"
    };

    private static NetworkAdapterInfo WiFi() => new()
    {
        Name = "Беспроводная сеть",
        Description = "TP-Link Wireless USB Adapter",
        Status = "Up"
    };

    private static RollingHealthWindow RollingHealthy()
    {
        var rolling = new RollingHealthWindow(20);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 20, PacketLossPercent = 0 });
        return rolling;
    }

    private static AppSettings Saved() => new()
    {
        RouteMode = VpnRouteMode.SeparateAdapter,
        TreatDefaultRouteChangeAsLeakRisk = true,
        ExpectedInterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)"
    };

    [Fact]
    public void Active_WhenSavedAdapterIsInTheLiveInventory()
    {
        var context = RouteCheckContext.FromAdapters(new[] { WiFi(), Karing() });

        Assert.Equal(RouteCheckState.Active, HealthEvaluator.GetRouteCheckState(Saved(), context));
    }

    [Fact]
    public void Active_WhenOnlyTheDriverDescriptionChanged()
    {
        var renamed = new NetworkAdapterInfo
        {
            Name = "Karing TUN Network Adapter",
            Description = "Karing TUN Adapter (v2 driver)",
            Status = "Up"
        };

        var context = RouteCheckContext.FromAdapters(new[] { WiFi(), renamed });

        Assert.Equal(RouteCheckState.Active, HealthEvaluator.GetRouteCheckState(Saved(), context));
    }

    [Fact]
    public void NeedsConfiguration_WhenTheAdapterIsGenuinelyGone()
    {
        var context = RouteCheckContext.FromAdapters(new[] { WiFi() });

        Assert.Equal(RouteCheckState.NeedsConfiguration, HealthEvaluator.GetRouteCheckState(Saved(), context));
    }

    [Fact]
    public void StartupRace_DoesNotFreezeTheApp_WhenAbsenceIsUnconfirmed()
    {
        // Monitor read the adapter list before the VPN service created the TUN. Until the tracker
        // confirms the absence, the context stays Unknown — and Unknown never declares a stale route.
        var tracker = new AdapterPresenceTracker(threshold: 3);
        var settings = Saved();
        var expected = ExpectedInterface.FromSettings(settings);
        var withoutTunnel = RouteCheckContext.FromAdapters(new[] { WiFi() });

        tracker.Observe(withoutTunnel.Contains(expected));
        var published = tracker.ConfirmedAbsent ? withoutTunnel : RouteCheckContext.Unknown;

        Assert.Equal(RouteCheckState.Active, HealthEvaluator.GetRouteCheckState(settings, published));
    }

    [Fact]
    public void PersistentAbsence_StillReachesNeedsConfiguration()
    {
        var tracker = new AdapterPresenceTracker(threshold: 3);
        var settings = Saved();
        var expected = ExpectedInterface.FromSettings(settings);
        var withoutTunnel = RouteCheckContext.FromAdapters(new[] { WiFi() });

        for (var i = 0; i < 3; i++)
        {
            tracker.Observe(withoutTunnel.Contains(expected));
        }

        var published = tracker.ConfirmedAbsent ? withoutTunnel : RouteCheckContext.Unknown;

        Assert.Equal(RouteCheckState.NeedsConfiguration, HealthEvaluator.GetRouteCheckState(settings, published));
    }

    [Fact]
    public void Evaluate_StaysOk_WhenDefaultRouteSitsOnTheSavedTunnel()
    {
        var snapshot = new NetworkSnapshot
        {
            CheckedAt = DateTimeOffset.Now,
            ExternalIPv4 = "185.229.191.52",
            Country = "NL",
            HttpAvailable = true,
            InterfaceName = "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)",
            PingAverageMs = 30,
            PacketLossPercent = 0,
            PingAttempts = 2,
            PingSuccesses = 2
        };

        var settings = Saved();
        settings.ExpectedCountry = "NL";

        var result = HealthEvaluator.Evaluate(
            snapshot,
            RollingHealthy(),
            settings,
            RouteCheckContext.FromAdapters(new[] { WiFi(), Karing() }));

        Assert.Equal(MonitorStatus.Ok, result.Status);
        Assert.Equal(RouteCheckState.Active, result.RouteCheck);
    }
}
