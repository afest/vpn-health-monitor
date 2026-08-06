using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Golden-master matrix for <see cref="HealthEvaluator.Evaluate"/>.
///
/// Each row pins the CURRENT priority ladder (HealthEvaluator.cs). These tests assert what the
/// code does *today*, not what it ideally should — a red row means behaviour changed and must be
/// reviewed, not silently re-baselined (see task T-182 "Anti-rationalize").
///
/// Note on the rolling window: Evaluate reads ping/loss from <see cref="RollingHealthWindow"/>, not
/// from the current snapshot. An EMPTY window reports PacketLossPercent = 100 (&gt; the 5% threshold),
/// which would short-circuit to Degraded. So every case that must reach a status *after* the Degraded
/// checks uses <see cref="RollingHealthy"/> (one low-ping, zero-loss sample).
/// </summary>
public class HealthEvaluatorTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public void Evaluate_Matrix(
        string name,
        NetworkSnapshot snapshot,
        AppSettings settings,
        RollingHealthWindow rolling,
        MonitorStatus expected,
        string descriptionFragment)
    {
        var result = HealthEvaluator.Evaluate(snapshot, rolling, settings);

        Assert.True(
            expected == result.Status,
            $"[{name}] expected status {expected} but got {result.Status}. Description: \"{result.Description}\"");

        Assert.True(
            result.Description.Contains(descriptionFragment, StringComparison.OrdinalIgnoreCase),
            $"[{name}] expected description to contain \"{descriptionFragment}\" but was \"{result.Description}\"");
    }

    public static IEnumerable<object[]> Cases()
    {
        // ---- Clean OK ---------------------------------------------------------------------------
        // Country matches, no leak flags, latency within limits → the all-clear.
        yield return Case("Ok_clean",
            Snap(ip: "203.0.113.7", country: "KZ", http: true),
            Settings(expectedCountry: "KZ"),
            RollingHealthy(),
            MonitorStatus.Ok, "в пределах настроек");

        // ---- NoInternet -------------------------------------------------------------------------
        // External IP unknown AND no HTTP AND no successful pings → nothing is reachable.
        yield return Case("NoInternet_all_dead",
            Snap(ip: null, http: false, pingSuccesses: 0),
            Settings(),
            RollingHealthy(),
            MonitorStatus.NoInternet, "недоступны");

        // ---- LeakRisk: country mismatch (risk on) -----------------------------------------------
        yield return Case("LeakRisk_country_mismatch",
            Snap(ip: "198.51.100.5", country: "SE", http: true),
            Settings(expectedCountry: "KZ"),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "Страна не совпадает");

        // ---- LeakRisk: external IPv4 not in expected list ---------------------------------------
        yield return Case("LeakRisk_ipv4_not_in_list",
            Snap(ip: "198.51.100.9", http: true),
            Settings(treatIpv4AsLeak: true, expectedIPv4: new() { "203.0.113.1" }),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "список разрешенных");

        // ---- LeakRisk: baseline IPv4 changed while changes are forbidden ------------------------
        yield return Case("LeakRisk_baseline_change_forbidden",
            Snap(ip: "198.51.100.9", http: true),
            Settings(treatIpv4AsLeak: true, allowIpChange: false,
                baseline: new BaselineInfo { IPv4 = "203.0.113.1" }),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "смена IP запрещена");

        // ---- LeakRisk: default-route changed, geo available (IP resolved) -----------------------
        yield return Case("LeakRisk_route_mismatch_geo_available",
            Snap(ip: "203.0.113.7", country: "KZ", iface: "Ethernet 2", http: true),
            Settings(expectedInterface: "VPN Tunnel"),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "VPN-маршрут сменился");

        // ---- LeakRisk: route changed AND geo API down (IP resolved, country unknown) -----------
        // The "недоступный geo API" branch the acceptance calls out explicitly (HealthEvaluator.cs L63).
        yield return Case("LeakRisk_route_mismatch_geo_unavailable",
            Snap(ip: "203.0.113.7", country: null, iface: "Ethernet 2", http: true),
            Settings(expectedCountry: "KZ", expectedInterface: "VPN Tunnel"),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "VPN-маршрут сменился");

        // ---- LeakRisk: route changed while external IP not yet resolved (HealthEvaluator.cs L37) -
        yield return Case("LeakRisk_route_mismatch_ip_pending",
            Snap(ip: null, iface: "Ethernet 2", http: true),
            Settings(expectedInterface: "VPN Tunnel"),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "пока не определены");

        // ---- LeakRisk: external IPv6 visible while forbidden ------------------------------------
        yield return Case("LeakRisk_external_ipv6_forbidden",
            Snap(ip: "203.0.113.7", ipv6: "2001:db8::dead", http: true),
            Settings(enableIpv6Check: true, allowExternalIpv6: false),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "IPv6 виден наружу");

        // ---- IpChanged: baseline IP changed, changes allowed, IPv4-risk on ----------------------
        yield return Case("IpChanged_baseline_allowed",
            Snap(ip: "203.0.113.99", http: true),
            Settings(treatIpv4AsLeak: true, allowIpChange: true,
                baseline: new BaselineInfo { IPv4 = "203.0.113.1" }),
            RollingHealthy(),
            MonitorStatus.IpChanged, "относительно baseline");

        // ---- CountryChanged: mismatch but country-risk turned off -------------------------------
        yield return Case("CountryChanged_risk_off",
            Snap(ip: "203.0.113.7", country: "SE", http: true),
            Settings(expectedCountry: "KZ", treatCountryAsLeak: false),
            RollingHealthy(),
            MonitorStatus.CountryChanged, "Страна изменилась");

        // ---- CountryUnknown: country unresolved but country-risk off ----------------------------
        yield return Case("CountryUnknown_risk_off",
            Snap(ip: "203.0.113.7", country: null, http: true),
            Settings(expectedCountry: "KZ", treatCountryAsLeak: false),
            RollingHealthy(),
            MonitorStatus.CountryUnknown, "Страну внешнего IPv4 не удалось определить");

        // ---- Degraded: rolling average ping over threshold --------------------------------------
        yield return Case("Degraded_high_ping",
            Snap(ip: "203.0.113.7", country: "KZ", http: true),
            Settings(expectedCountry: "KZ"),
            RollingWith(pingMs: 300, lossPercent: 0),
            MonitorStatus.Degraded, "Средний ping");

        // ---- Degraded: rolling packet loss over threshold ---------------------------------------
        yield return Case("Degraded_packet_loss",
            Snap(ip: "203.0.113.7", country: "KZ", http: true),
            Settings(expectedCountry: "KZ"),
            RollingWith(pingMs: 20, lossPercent: 20),
            MonitorStatus.Degraded, "Потери пакетов");

        // ---- CheckFailed: internet up but IP API gave nothing -----------------------------------
        yield return Case("CheckFailed_ip_api",
            Snap(ip: null, http: true),
            Settings(expectedCountry: "KZ"),
            RollingHealthy(),
            MonitorStatus.CheckFailed, "API внешнего IPv4 не ответили");

        // ---- CheckFailed: IP resolved but country API failed, no route mismatch -----------------
        yield return Case("CheckFailed_country_only",
            Snap(ip: "203.0.113.7", country: null, http: true),
            Settings(expectedCountry: "KZ"),
            RollingHealthy(),
            MonitorStatus.CheckFailed, "страну не удалось проверить");

        // ---- VpnDown: ping alive but HTTP + all IP APIs dead, VPN configured ---------------------
        yield return Case("VpnDown_proxy_tunnel_dropped",
            Snap(ip: null, http: false, pingSuccesses: 3),
            Settings(expectedCountry: "KZ"),
            RollingHealthy(),
            MonitorStatus.VpnDown, "VPN, похоже, выключен");

        // ---- Informational OK: baseline IP changed but all risk flags off -----------------------
        yield return Case("InformationalOk_baseline_changed",
            Snap(ip: "203.0.113.99", http: true),
            Settings(treatIpv4AsLeak: false,
                baseline: new BaselineInfo { IPv4 = "203.0.113.1" }),
            RollingHealthy(),
            MonitorStatus.Ok, "относительно baseline");

        // ---- Informational OK: IPv4 outside list but IPv4-risk off ------------------------------
        yield return Case("InformationalOk_ip_off_list",
            Snap(ip: "198.51.100.9", http: true),
            Settings(treatIpv4AsLeak: false, expectedIPv4: new() { "203.0.113.1" }),
            RollingHealthy(),
            MonitorStatus.Ok, "вне списка");

        // ---- PRIORITY: country mismatch outranks route mismatch (both → LeakRisk; L75 before L82) -
        // Description proves WHICH branch won.
        yield return Case("Priority_country_over_route",
            Snap(ip: "203.0.113.7", country: "SE", iface: "Ethernet 2", http: true),
            Settings(expectedCountry: "KZ", expectedInterface: "VPN Tunnel"),
            RollingHealthy(),
            MonitorStatus.LeakRisk, "Страна не совпадает");

        // ---- PRIORITY: Degraded outranks CountryChanged (L107 before L117; observable via status) -
        yield return Case("Priority_degraded_over_country_changed",
            Snap(ip: "203.0.113.7", country: "SE", http: true),
            Settings(expectedCountry: "KZ", treatCountryAsLeak: false),
            RollingWith(pingMs: 20, lossPercent: 20),
            MonitorStatus.Degraded, "Потери пакетов");
    }

    // ---- builders -------------------------------------------------------------------------------

    private static object[] Case(
        string name,
        NetworkSnapshot snapshot,
        AppSettings settings,
        RollingHealthWindow rolling,
        MonitorStatus expected,
        string descriptionFragment)
        => new object[] { name, snapshot, settings, rolling, expected, descriptionFragment };

    private static NetworkSnapshot Snap(
        string? ip = null,
        string? country = null,
        string? ipv6 = null,
        string? iface = null,
        bool http = false,
        int pingSuccesses = 0)
        => new()
        {
            ExternalIPv4 = ip,
            Country = country,
            ExternalIPv6 = ipv6,
            InterfaceName = iface,
            HttpAvailable = http,
            PingSuccesses = pingSuccesses
        };

    private static AppSettings Settings(
        string expectedCountry = "",
        bool treatCountryAsLeak = true,
        bool treatIpv4AsLeak = false,
        bool allowIpChange = true,
        List<string>? expectedIPv4 = null,
        string expectedInterface = "",
        bool treatRouteAsLeak = true,
        bool enableIpv6Check = false,
        bool allowExternalIpv6 = true,
        BaselineInfo? baseline = null)
        => new()
        {
            ExpectedCountry = expectedCountry,
            TreatCountryMismatchAsLeakRisk = treatCountryAsLeak,
            TreatUnexpectedIPv4AsLeakRisk = treatIpv4AsLeak,
            AllowIpChangesWithinExpectedCountry = allowIpChange,
            ExpectedPublicIPv4 = expectedIPv4 ?? new(),
            ExpectedInterfaceName = expectedInterface,
            TreatDefaultRouteChangeAsLeakRisk = treatRouteAsLeak,
            EnableIPv6LeakCheck = enableIpv6Check,
            AllowExternalIPv6 = allowExternalIpv6,
            Baseline = baseline
        };

    private static RollingHealthWindow RollingHealthy() => RollingWith(pingMs: 20, lossPercent: 0);

    private static RollingHealthWindow RollingWith(double pingMs, double lossPercent)
    {
        var window = new RollingHealthWindow(10);
        window.Add(new NetworkSnapshot { PingAverageMs = pingMs, PacketLossPercent = lossPercent });
        return window;
    }
}
