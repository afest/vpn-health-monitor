using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// T-473: одиночный сбой проверки не засчитывается, серия и уход с туннеля — засчитываются сразу.
/// Результат строит настоящий <see cref="HealthEvaluator"/>, как в окне: проверяется вся цепочка
/// «замер → вердикт → фильтр», а не фильтр на выдуманных статусах.
/// </summary>
public class ConnectivityFailureFilterTests
{
    private const string Vpn = "hidemy.name VPN OpenVPN Adapter (TAP-Windows Adapter V9)";
    private const string WiFi = "Беспроводная сеть (Wi-Fi USB Adapter)";
    private const string VpnIp = "203.0.113.10";
    private const string HomeIp = "198.51.100.77";

    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 11, 37, 38, TimeSpan.FromHours(5));

    [Fact]
    public void Single_failure_on_the_vpn_adapter_is_held()
    {
        var filter = new ConnectivityFailureFilter();

        var decision = Check(filter, Dead(T0, Vpn));

        Assert.Equal(MonitorStatus.NoInternet, Evaluate(Dead(T0, Vpn)).Status);
        Assert.Equal(FailureFilterAction.Hold, decision.Action);
    }

    [Fact]
    public void Second_failure_in_a_row_is_confirmed()
    {
        var filter = new ConnectivityFailureFilter();

        Check(filter, Dead(T0, Vpn));
        var second = Check(filter, Dead(T0.AddSeconds(13), Vpn));

        Assert.Equal(FailureFilterAction.Confirmed, second.Action);
        Assert.Equal(FailureConfirmation.Consecutive, second.Reason);
    }

    [Fact]
    public void Healthy_check_between_failures_resets_the_streak()
    {
        var filter = new ConnectivityFailureFilter();

        Assert.Equal(FailureFilterAction.Hold, Check(filter, Dead(T0, Vpn)).Action);
        Assert.Equal(FailureFilterAction.NotAFailure, Check(filter, Healthy(T0.AddSeconds(5))).Action);
        Assert.Equal(FailureFilterAction.Hold, Check(filter, Dead(T0.AddSeconds(200), Vpn)).Action);
    }

    // Сетка из журналов 17–26.09: сбой раз в ~301 с, между ними проверки идут нормально.
    // 298 с — самый короткий интервал, который встретился в журнале.
    [Theory]
    [InlineData(298)]
    [InlineData(301)]
    public void Five_minute_grid_never_raises_an_alarm(int periodSeconds)
    {
        var filter = new ConnectivityFailureFilter();

        for (var i = 0; i < 24; i++)
        {
            var failedAt = T0.AddSeconds(i * periodSeconds);
            var decision = Check(filter, Dead(failedAt, Vpn));
            Check(filter, Healthy(failedAt.AddSeconds(5)));

            Assert.Equal(FailureFilterAction.Hold, decision.Action);
        }
    }

    // Серия 24.09 на личном VPS: одиночные сбои каждые 40–60 с. Каждый по отдельности придержан
    // бы, вместе они — нестабильная связь, и её видно с третьего сбоя.
    [Fact]
    public void Series_of_single_failures_is_confirmed_from_the_third()
    {
        var filter = new ConnectivityFailureFilter();
        var offsets = new[] { 0, 45, 96, 140, 199 };
        var decisions = new List<FailureFilterDecision>();

        foreach (var offset in offsets)
        {
            decisions.Add(Check(filter, Dead(T0.AddSeconds(offset), Vpn)));
            Check(filter, Healthy(T0.AddSeconds(offset + 9)));
        }

        Assert.Equal(FailureFilterAction.Hold, decisions[0].Action);
        Assert.Equal(FailureFilterAction.Hold, decisions[1].Action);
        Assert.All(decisions.Skip(2), decision =>
        {
            Assert.Equal(FailureFilterAction.Confirmed, decision.Action);
            Assert.Equal(FailureConfirmation.Repeated, decision.Reason);
        });
        Assert.Equal(5, decisions[^1].FailuresInWindow);
    }

    [Fact]
    public void Old_failures_drop_out_of_the_window()
    {
        var filter = new ConnectivityFailureFilter();

        Check(filter, Dead(T0, Vpn));
        Check(filter, Healthy(T0.AddSeconds(5)));
        Check(filter, Dead(T0.AddSeconds(100), Vpn));
        Check(filter, Healthy(T0.AddSeconds(105)));
        var third = Check(filter, Dead(T0.AddSeconds(301), Vpn));

        Assert.Equal(FailureFilterAction.Hold, third.Action);
        Assert.Equal(2, third.FailuresInWindow);
    }

    // Главное условие задачи: уход маршрута с VPN-адаптера не ждёт второй проверки, даже если
    // в том же цикле не ответило ничего (26.09 09:32:17 — HideMe переподключался через Wi-Fi).
    [Fact]
    public void Failure_with_route_on_physical_adapter_is_shown_at_once()
    {
        var filter = new ConnectivityFailureFilter();

        var decision = Check(filter, Dead(T0, WiFi));

        Assert.Equal(FailureFilterAction.Confirmed, decision.Action);
        Assert.Equal(FailureConfirmation.RouteOrAddressChanged, decision.Reason);
    }

    // Риск утечки — не провал связи, фильтр его не трогает ни на первом цикле, ни посреди
    // придержанного сбоя.
    [Fact]
    public void Leak_risk_is_never_held()
    {
        var filter = new ConnectivityFailureFilter();
        Check(filter, Dead(T0, Vpn));

        var directExit = new NetworkSnapshot
        {
            CheckedAt = T0.AddSeconds(5),
            ExternalIPv4 = HomeIp,
            Country = "RU",
            InterfaceName = WiFi,
            HttpAvailable = true,
            PingSuccesses = 2,
            PingAttempts = 2,
            PacketLossPercent = 0,
            PingAverageMs = 40
        };
        var routeOnly = new NetworkSnapshot
        {
            CheckedAt = T0.AddSeconds(10),
            InterfaceName = WiFi,
            HttpAvailable = true,
            PingSuccesses = 1,
            PingAttempts = 2,
            PacketLossPercent = 50
        };

        Assert.Equal(MonitorStatus.LeakRisk, Evaluate(directExit).Status);
        Assert.Equal(FailureFilterAction.NotAFailure, Check(filter, directExit).Action);
        Assert.Equal(MonitorStatus.LeakRisk, Evaluate(routeOnly).Status);
        Assert.Equal(FailureFilterAction.NotAFailure, Check(filter, routeOnly).Action);
    }

    [Fact]
    public void Failed_check_with_a_new_external_ip_is_shown_at_once()
    {
        var filter = new ConnectivityFailureFilter();

        var decision = Check(filter, GeoFailed(T0, "203.0.113.50"), lastKnownIPv4: VpnIp);

        Assert.Equal(MonitorStatus.CheckFailed, Evaluate(GeoFailed(T0, "203.0.113.50")).Status);
        Assert.Equal(FailureFilterAction.Confirmed, decision.Action);
        Assert.Equal(FailureConfirmation.RouteOrAddressChanged, decision.Reason);
    }

    [Fact]
    public void Failed_geo_lookup_on_the_same_ip_is_held()
    {
        var filter = new ConnectivityFailureFilter();

        var decision = Check(filter, GeoFailed(T0, VpnIp), lastKnownIPv4: VpnIp);

        Assert.Equal(FailureFilterAction.Hold, decision.Action);
    }

    [Fact]
    public void Missing_default_route_is_shown_at_once()
    {
        var filter = new ConnectivityFailureFilter();

        var decision = Check(filter, Dead(T0, "Unknown"));

        Assert.Equal(FailureFilterAction.Confirmed, decision.Action);
        Assert.Equal(FailureConfirmation.NoRoute, decision.Reason);
    }

    [Fact]
    public void Manual_check_is_shown_at_once()
    {
        var filter = new ConnectivityFailureFilter();

        var decision = Check(filter, Dead(T0, Vpn), manual: true);

        Assert.Equal(FailureFilterAction.Confirmed, decision.Action);
        Assert.Equal(FailureConfirmation.Manual, decision.Reason);
    }

    // Режим без адаптера: маршрут не проверяется, «VPN отключён» — это провал связи, и он тоже
    // ждёт второй проверки. Как только трафик пойдёт напрямую, это будет риск утечки по стране.
    [Fact]
    public void Proxy_vpn_down_waits_for_a_second_check()
    {
        var filter = new ConnectivityFailureFilter();
        var settings = new AppSettings
        {
            RouteMode = VpnRouteMode.NoSeparateAdapter,
            ExpectedCountry = "DE"
        };
        NetworkSnapshot ProxyDead(DateTimeOffset at) => new()
        {
            CheckedAt = at,
            InterfaceName = WiFi,
            HttpAvailable = false,
            PingSuccesses = 2,
            PingAttempts = 2,
            PacketLossPercent = 0
        };

        Assert.Equal(MonitorStatus.VpnDown, Evaluate(ProxyDead(T0), settings).Status);
        Assert.Equal(FailureFilterAction.Hold, Check(filter, ProxyDead(T0), settings: settings).Action);
        Assert.Equal(FailureFilterAction.Confirmed, Check(filter, ProxyDead(T0.AddSeconds(13)), settings: settings).Action);
    }

    // 28.09 19:59 на 1.2.8: IP API ответил, HTTP и ping — нет. Вердикт остался «OK», а в журнал
    // всё равно ушло «интернет пропал». Один такой цикл — сетка, два подряд — уже пропажа.
    [Fact]
    public void Internet_loss_with_answering_ip_api_needs_two_silent_cycles()
    {
        var filter = new ConnectivityFailureFilter();
        NetworkSnapshot Silent(DateTimeOffset at) => new()
        {
            CheckedAt = at,
            ExternalIPv4 = VpnIp,
            Country = "DE",
            InterfaceName = Vpn,
            HttpAvailable = false,
            PingSuccesses = 0,
            PingAttempts = 2,
            PacketLossPercent = 100
        };

        var first = Silent(T0);
        var firstResult = Evaluate(first);
        Assert.Equal(FailureFilterAction.NotAFailure, filter.Observe(firstResult, first, VpnSettings(), VpnIp).Action);
        Assert.False(filter.IsInternetLossConfirmed(first, firstResult.Status));

        var second = Silent(T0.AddSeconds(13));
        var secondResult = Evaluate(second);
        filter.Observe(secondResult, second, VpnSettings(), VpnIp);
        Assert.True(filter.IsInternetLossConfirmed(second, secondResult.Status));
    }

    [Fact]
    public void Shown_no_internet_confirms_internet_loss_at_once()
    {
        var filter = new ConnectivityFailureFilter();
        var snapshot = Dead(T0, WiFi);
        var result = Evaluate(snapshot);

        Assert.Equal(FailureFilterAction.Confirmed, filter.Observe(result, snapshot, VpnSettings(), VpnIp).Action);
        Assert.True(filter.IsInternetLossConfirmed(snapshot, result.Status));
    }

    [Fact]
    public void Reset_forgets_earlier_failures()
    {
        var filter = new ConnectivityFailureFilter();
        Check(filter, Dead(T0, Vpn));

        filter.Reset();

        Assert.Equal(FailureFilterAction.Hold, Check(filter, Dead(T0.AddSeconds(13), Vpn)).Action);
    }

    private static FailureFilterDecision Check(
        ConnectivityFailureFilter filter,
        NetworkSnapshot snapshot,
        string? lastKnownIPv4 = VpnIp,
        bool manual = false,
        AppSettings? settings = null)
    {
        settings ??= VpnSettings();
        return filter.Observe(Evaluate(snapshot, settings), snapshot, settings, lastKnownIPv4, manual);
    }

    private static HealthResult Evaluate(NetworkSnapshot snapshot, AppSettings? settings = null)
    {
        var rolling = new RollingHealthWindow(20);
        rolling.Add(new NetworkSnapshot { PingAverageMs = 40, PacketLossPercent = 0 });
        return HealthEvaluator.Evaluate(snapshot, rolling, settings ?? VpnSettings());
    }

    private static AppSettings VpnSettings() => new()
    {
        RouteMode = VpnRouteMode.SeparateAdapter,
        TreatDefaultRouteChangeAsLeakRisk = true,
        AllowedVpnInterfaces = new List<string> { Vpn },
        ExpectedInterfaceName = Vpn,
        ExpectedCountry = "DE",
        TreatCountryMismatchAsLeakRisk = true
    };

    private static NetworkSnapshot Dead(DateTimeOffset at, string interfaceName) => new()
    {
        CheckedAt = at,
        InterfaceName = interfaceName,
        HttpAvailable = false,
        PingSuccesses = 0,
        PingAttempts = 2,
        PacketLossPercent = 100
    };

    private static NetworkSnapshot GeoFailed(DateTimeOffset at, string ip) => new()
    {
        CheckedAt = at,
        ExternalIPv4 = ip,
        InterfaceName = Vpn,
        HttpAvailable = true,
        PingSuccesses = 2,
        PingAttempts = 2,
        PacketLossPercent = 0
    };

    private static NetworkSnapshot Healthy(DateTimeOffset at) => new()
    {
        CheckedAt = at,
        ExternalIPv4 = VpnIp,
        Country = "DE",
        InterfaceName = Vpn,
        HttpAvailable = true,
        PingSuccesses = 2,
        PingAttempts = 2,
        PacketLossPercent = 0,
        PingAverageMs = 78
    };
}
