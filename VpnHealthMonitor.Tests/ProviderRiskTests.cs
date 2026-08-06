using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-325: risk by exit provider — the only check that still fires when the VPN exits in the user's own
/// country. The rule is worth having only if it stays quiet on server rotation and on missing geo data:
/// a check that cries wolf gets switched off, and then nothing guards anything.
/// </summary>
public class ProviderRiskTests
{
    private static AppSettings Settings(bool enabled = true, params string[] allowed) => new()
    {
        TreatProviderChangeAsLeakRisk = enabled,
        AllowedProviders = allowed.Select(ProviderMatcher.ParseIdentity).Where(p => p is not null).Select(p => p!).ToList()
    };

    private static NetworkSnapshot Snap(string? asn, string? provider) => new()
    {
        ExternalIPv4 = "203.0.113.7",
        Country = "KZ",
        Asn = asn,
        Provider = provider,
        HttpAvailable = true,
        PingSuccesses = 2,
        PingAttempts = 2
    };

    // ---- Matching ------------------------------------------------------------------------------

    [Theory]
    [InlineData("AS9009", "M247 Europe SRL", "AS9009", "M247 Ltd")]              // same ASN, different spelling
    [InlineData("AS9009", "M247 Europe SRL", "AS396356", "M247 Ltd")]            // rotation: new ASN, same company
    [InlineData(null, "Cloudflare, Inc.", null, "CLOUDFLARENET")]                // prefix form
    [InlineData(null, "Datapacket Ltd", null, "DataPacket, s.r.o.")]             // legal-form noise
    [InlineData("AS13335", null, "AS13335", "Cloudflare")]                       // ASN only on one side
    public void SameProvider_IsRecognised(string? asnA, string? nameA, string? asnB, string? nameB)
    {
        Assert.True(ProviderMatcher.IsSameProvider(asnA, nameA, asnB, nameB));
    }

    [Theory]
    [InlineData("AS9009", "M247 Europe SRL", "AS31133", "PJSC MegaFon")]
    [InlineData(null, "Mullvad VPN AB", null, "Rostelecom")]
    [InlineData("AS9009", "M247", null, null)]                                   // unknown side never matches
    public void DifferentProvider_IsNotMatched(string? asnA, string? nameA, string? asnB, string? nameB)
    {
        Assert.False(ProviderMatcher.IsSameProvider(asnA, nameA, asnB, nameB));
    }

    // ---- The rule ------------------------------------------------------------------------------

    /// <summary>The scenario the whole rule exists for: tunnel dropped, exit is now the home ISP.</summary>
    [Fact]
    public void HomeIsp_AfterVpnDrop_IsLeakRisk()
    {
        var settings = Settings(true, "M247 Europe SRL (AS9009)");
        var result = HealthEvaluator.Evaluate(Snap("AS31133", "PJSC MegaFon"), RollingHealthy(), settings);

        Assert.Equal(MonitorStatus.LeakRisk, result.Status);
        Assert.Contains("Провайдер выхода", result.Description);
    }

    /// <summary>Acceptance: rotating exits inside one hosting company must not fire.</summary>
    [Fact]
    public void ServerRotationInsideSameHoster_IsSilent()
    {
        var settings = Settings(true, "M247 Europe SRL (AS9009)");

        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap("AS396356", "M247 Ltd"), settings));
        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap(null, "M247"), settings));
    }

    [Fact]
    public void SecondAllowedProvider_IsAccepted()
    {
        var settings = Settings(true, "M247 Europe SRL (AS9009)", "DataPacket");

        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap("AS212238", "Datapacket Ltd"), settings));
    }

    [Fact]
    public void NoProviderData_IsSilent()
    {
        var settings = Settings(true, "M247 Europe SRL (AS9009)");

        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap(null, null), settings));
        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap("", "   "), settings));
    }

    [Fact]
    public void EmptyAllowList_IsSilent()
    {
        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap("AS31133", "PJSC MegaFon"), Settings(true)));
    }

    [Fact]
    public void CheckOff_IsSilent()
    {
        var settings = Settings(false, "M247 Europe SRL (AS9009)");

        Assert.False(HealthEvaluator.ProviderIsUnexpected(Snap("AS31133", "PJSC MegaFon"), settings));
    }

    /// <summary>Country risk is the more specific signal and must still win the ladder.</summary>
    [Fact]
    public void CountryMismatch_StillOutranksProviderRisk()
    {
        var settings = Settings(true, "M247 Europe SRL (AS9009)");
        settings.ExpectedCountry = "KZ";

        var snapshot = new NetworkSnapshot
        {
            ExternalIPv4 = "198.51.100.5",
            Country = "SE",
            Asn = "AS31133",
            Provider = "PJSC MegaFon",
            HttpAvailable = true,
            PingSuccesses = 2,
            PingAttempts = 2
        };

        var result = HealthEvaluator.Evaluate(snapshot, RollingHealthy(), settings);

        Assert.Equal(MonitorStatus.LeakRisk, result.Status);
        Assert.Contains("Страна не совпадает", result.Description);
    }

    // ---- Settings round-trip -------------------------------------------------------------------

    [Theory]
    [InlineData("M247 Europe SRL (AS9009)", "AS9009", "M247 Europe SRL")]
    [InlineData("AS13335", "AS13335", null)]
    [InlineData("Mullvad VPN AB", null, "Mullvad VPN AB")]
    [InlineData("Some Host (not an asn)", null, "Some Host (not an asn)")]
    public void ParseIdentity_ReadsSettingsLines(string line, string? expectedAsn, string? expectedName)
    {
        var identity = ProviderMatcher.ParseIdentity(line);

        Assert.NotNull(identity);
        Assert.Equal(expectedAsn, identity!.Asn);
        Assert.Equal(expectedName, identity.Name);
    }

    [Fact]
    public void ParseIdentity_IgnoresBlankLines()
    {
        Assert.Null(ProviderMatcher.ParseIdentity("   "));
        Assert.Null(ProviderMatcher.ParseIdentity(null));
    }

    [Fact]
    public void DescribeRoundTripsThroughParse()
    {
        var described = ProviderMatcher.Describe("AS9009", "M247 Europe SRL");
        var parsed = ProviderMatcher.ParseIdentity(described);

        Assert.Equal("AS9009", parsed!.Asn);
        Assert.Equal("M247 Europe SRL", parsed.Name);
    }

    /// <summary>One healthy sample: an empty window reports 100% loss and would short-circuit to Degraded.</summary>
    private static RollingHealthWindow RollingHealthy()
    {
        var rolling = new RollingHealthWindow(20);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 10, PacketLossPercent = 0 });
        return rolling;
    }
}
