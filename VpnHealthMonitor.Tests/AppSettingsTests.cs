using System.Text.Json;
using VpnHealthMonitor.Models;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Per-type notification toggles (T-201). Pins the delivery-gate contract:
/// only the noisy statuses (IpChanged / CountryChanged) are gated by a toggle; every safety or
/// neutral status always delivers. Also pins backward-compat: an old settings.json written before
/// T-201 (neither field present) must keep the intended defaults — Country on, IP off — not collapse
/// both to false. These are pure-function / serialization tests, so they prove the gate without a
/// live tray run (the live IP/country smoke is a manual review step).
/// </summary>
public class AppSettingsTests
{
    [Fact]
    public void Defaults_CountryOn_IpOff()
    {
        var settings = new AppSettings();

        Assert.True(settings.NotifyCountryChanged);
        Assert.False(settings.NotifyIpChanged);
    }

    [Theory]
    // Noisy statuses follow their toggle (defaults: Country on, Ip off);
    // everything else — safety and neutral — always delivers.
    [InlineData(MonitorStatus.IpChanged, false)]
    [InlineData(MonitorStatus.CountryChanged, true)]
    [InlineData(MonitorStatus.LeakRisk, true)]
    [InlineData(MonitorStatus.NoInternet, true)]
    [InlineData(MonitorStatus.VpnDown, true)]
    [InlineData(MonitorStatus.Degraded, true)]
    [InlineData(MonitorStatus.CheckFailed, true)]
    [InlineData(MonitorStatus.CountryUnknown, true)]
    [InlineData(MonitorStatus.Ok, true)]
    public void ShouldNotifyForStatus_Defaults(MonitorStatus status, bool expected)
    {
        var settings = new AppSettings();

        Assert.Equal(expected, settings.ShouldNotifyForStatus(status));
    }

    [Fact]
    public void ShouldNotifyForStatus_RespectsToggles()
    {
        var settings = new AppSettings { NotifyIpChanged = true, NotifyCountryChanged = false };

        Assert.True(settings.ShouldNotifyForStatus(MonitorStatus.IpChanged));
        Assert.False(settings.ShouldNotifyForStatus(MonitorStatus.CountryChanged));
        // Safety status ignores the toggles — never silenceable here.
        Assert.True(settings.ShouldNotifyForStatus(MonitorStatus.LeakRisk));
    }

    [Fact]
    public void LegacySettingsJson_WithoutToggles_KeepsDefaults()
    {
        // A settings.json predating T-201 has neither toggle field.
        const string legacyJson = "{ \"IntervalSeconds\": 5, \"EnableWindowsNotifications\": true }";

        var settings = JsonSerializer.Deserialize<AppSettings>(
            legacyJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(settings);
        Assert.True(settings!.NotifyCountryChanged);
        Assert.False(settings.NotifyIpChanged);
    }
}
