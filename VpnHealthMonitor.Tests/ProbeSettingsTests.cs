using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-325: addresses used for the reachability checks are settings, not constants — in a network where
/// Google and Cloudflare are blocked the hard-wired list meant a permanent "НЕТ ИНТЕРНЕТА". And an address
/// the user deliberately removed must stay removed.
/// </summary>
public class ProbeSettingsTests
{
    [Fact]
    public void FreshSettingsCarryTheDefaultProbes()
    {
        var settings = SettingsService.Normalize(new AppSettings());

        Assert.Equal(NetworkCheckService.DefaultHttpProbeUrls, settings.HttpProbeUrls);
        Assert.Equal(NetworkCheckService.DefaultPingHosts, settings.PingHosts);
    }

    [Fact]
    public void RemovedProbeIsNotRestored()
    {
        var settings = new AppSettings
        {
            HttpProbeUrls = new List<string> { "https://mirror.example/health" },
            PingHosts = new List<string> { "9.9.9.9" }
        };

        var normalized = SettingsService.Normalize(settings);

        Assert.Equal(new[] { "https://mirror.example/health" }, normalized.HttpProbeUrls);
        Assert.Equal(new[] { "9.9.9.9" }, normalized.PingHosts);
    }

    /// <summary>The same bug lived in the IP/geo lists: a deleted endpoint came back on every save.</summary>
    [Fact]
    public void RemovedIpApiEndpointIsNotRestored()
    {
        var settings = new AppSettings
        {
            IpApiEndpoints = new List<string> { "https://api.ipify.org?format=json" },
            IPv6ApiEndpoints = new List<string> { "https://api6.ipify.org?format=json" }
        };

        var normalized = SettingsService.Normalize(settings);

        Assert.Equal(new[] { "https://api.ipify.org?format=json" }, normalized.IpApiEndpoints);
        Assert.Equal(new[] { "https://api6.ipify.org?format=json" }, normalized.IPv6ApiEndpoints);
    }

    [Fact]
    public void EmptyListMeansRestoreDefaults()
    {
        var settings = SettingsService.Normalize(new AppSettings
        {
            HttpProbeUrls = new List<string>(),
            PingHosts = new List<string>(),
            IpApiEndpoints = new List<string>()
        });

        Assert.NotEmpty(settings.HttpProbeUrls);
        Assert.NotEmpty(settings.PingHosts);
        Assert.NotEmpty(settings.IpApiEndpoints);
    }

    [Fact]
    public void NoPlainHttpEndpointsRemain()
    {
        var settings = SettingsService.Normalize(new AppSettings());

        Assert.All(settings.IpApiEndpoints, url => Assert.StartsWith("https://", url));
        Assert.All(settings.IPv6ApiEndpoints, url => Assert.StartsWith("https://", url));
        Assert.All(settings.HttpProbeUrls, url => Assert.StartsWith("https://", url));
    }

    /// <summary>
    /// Acceptance: with every external service dead but pings alive, the verdict must not be "нет интернета".
    /// </summary>
    [Fact]
    public void PingsAlive_ButAllServicesDown_IsNotNoInternet()
    {
        var snapshot = new NetworkSnapshot
        {
            ExternalIPv4 = null,
            HttpAvailable = false,
            PingSuccesses = 2,
            PingAttempts = 2
        };

        var rolling = new RollingHealthWindow(20);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 12, PacketLossPercent = 0 });

        var result = HealthEvaluator.Evaluate(snapshot, rolling, new AppSettings());

        Assert.NotEqual(MonitorStatus.NoInternet, result.Status);
        Assert.Equal(MonitorStatus.CheckFailed, result.Status);
    }
}
