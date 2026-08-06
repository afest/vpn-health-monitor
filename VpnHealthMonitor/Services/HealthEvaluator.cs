using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

public static class HealthEvaluator
{
    /// <summary>
    /// Does the "VPN route" risk check actually guard anything (T-323)? Enabled + a VPN-looking expected
    /// interface = it can fire. Enabled against an empty or non-VPN interface (system-proxy mode keeps the
    /// default route on Wi-Fi forever) = it can never fire, and the UI must say so instead of showing green.
    /// </summary>
    public static RouteCheckState GetRouteCheckState(AppSettings settings)
    {
        if (!settings.TreatDefaultRouteChangeAsLeakRisk)
        {
            return RouteCheckState.Disabled;
        }

        var expected = GetExpectedInterfaceName(settings);
        return VpnInterfaceHeuristics.LooksLikeVpn(expected)
            ? RouteCheckState.Active
            : RouteCheckState.NotApplicable;
    }

    public static HealthResult Evaluate(NetworkSnapshot snapshot, RollingHealthWindow rolling, AppSettings settings)
    {
        var result = EvaluateStatus(snapshot, rolling, settings);
        return new HealthResult
        {
            Status = result.Status,
            Description = result.Description,
            RouteCheck = GetRouteCheckState(settings)
        };
    }

    private static HealthResult EvaluateStatus(NetworkSnapshot snapshot, RollingHealthWindow rolling, AppSettings settings)
    {
        var internetAvailable = snapshot.HttpAvailable || snapshot.PingSuccesses > 0;
        var expectedCountryIsSet = !string.IsNullOrWhiteSpace(settings.ExpectedCountry);
        var countryMissing = expectedCountryIsSet && string.IsNullOrWhiteSpace(snapshot.Country);
        var countryMismatch = expectedCountryIsSet && !countryMissing && !CountryMatches(settings.ExpectedCountry, snapshot.Country);
        var expectedIpMismatch = settings.ExpectedPublicIPv4.Count > 0
            && !settings.ExpectedPublicIPv4.Contains(snapshot.ExternalIPv4, StringComparer.OrdinalIgnoreCase);
        var baselineIpChanged = !string.IsNullOrWhiteSpace(settings.Baseline?.IPv4)
            && !string.Equals(settings.Baseline.IPv4, snapshot.ExternalIPv4, StringComparison.OrdinalIgnoreCase);
        var unexpectedExternalIPv6 = settings.EnableIPv6LeakCheck
            && !settings.AllowExternalIPv6
            && !string.IsNullOrWhiteSpace(snapshot.ExternalIPv6);
        var expectedInterfaceName = GetExpectedInterfaceName(settings);
        var defaultRouteMismatch = settings.TreatDefaultRouteChangeAsLeakRisk
            && !string.IsNullOrWhiteSpace(expectedInterfaceName)
            && !InterfaceMatches(expectedInterfaceName, snapshot.InterfaceName);

        if (!snapshot.IpLookupSucceeded && !internetAvailable)
        {
            return Result(MonitorStatus.NoInternet, "Внешний IP, HTTP-проверки и ping недоступны.");
        }

        if (unexpectedExternalIPv6)
        {
            return Result(
                MonitorStatus.LeakRisk,
                $"Внешний IPv6 виден наружу ({snapshot.ExternalIPv6}), а в настройках внешний IPv6 запрещен.");
        }

        if (defaultRouteMismatch && !snapshot.IpLookupSucceeded)
        {
            return Result(
                MonitorStatus.LeakRisk,
                BuildRouteMismatchDescription(expectedInterfaceName, snapshot.InterfaceName, "Внешний IPv4/страна пока не определены."));
        }

        // Proxy-VPN отвалился: ICMP-пинг проходит (сеть физически жива), но и HTTP-доступность,
        // и все API внешнего IPv4 разом недоступны — для proxy-режима это типичный признак обрыва
        // туннеля (трафик через мёртвый прокси отрезан, реальный IP наружу не определить).
        // Отличаем от обычного сбоя только IP API: там HttpAvailable остаётся true.
        if (!snapshot.IpLookupSucceeded
            && !snapshot.HttpAvailable
            && snapshot.PingSuccesses > 0
            && VpnConfigured(settings, expectedInterfaceName))
        {
            return Result(
                MonitorStatus.VpnDown,
                "VPN, похоже, выключен: ICMP-пинг проходит, но HTTP-доступность и все API внешнего IPv4 недоступны — для proxy-VPN это типичный признак обрыва туннеля.");
        }

        if (!snapshot.IpLookupSucceeded)
        {
            return Result(MonitorStatus.CheckFailed, "Интернет выглядит доступным, но API внешнего IPv4 не ответили корректно.");
        }

        if (settings.TreatCountryMismatchAsLeakRisk && countryMissing && defaultRouteMismatch)
        {
            return Result(
                MonitorStatus.LeakRisk,
                BuildRouteMismatchDescription(expectedInterfaceName, snapshot.InterfaceName, "Внешний IPv4 получен, но страну не удалось проверить."));
        }

        if (settings.TreatCountryMismatchAsLeakRisk && countryMissing)
        {
            return Result(MonitorStatus.CheckFailed, "Внешний IPv4 получен, но страну не удалось проверить.");
        }

        if (settings.TreatCountryMismatchAsLeakRisk && countryMismatch)
        {
            return Result(
                MonitorStatus.LeakRisk,
                $"Страна не совпадает: ожидалось {CountryNames.ToDisplayName(settings.ExpectedCountry)}, сейчас {CountryNames.ToDisplayName(snapshot.Country)}.");
        }

        if (defaultRouteMismatch)
        {
            return Result(
                MonitorStatus.LeakRisk,
                BuildRouteMismatchDescription(expectedInterfaceName, snapshot.InterfaceName, "Default route больше не похож на сохраненный VPN-маршрут."));
        }

        if (ProviderIsUnexpected(snapshot, settings))
        {
            return Result(
                MonitorStatus.LeakRisk,
                $"Провайдер выхода не в списке разрешённых: сейчас {ProviderMatcher.Describe(snapshot.Asn, snapshot.Provider)}. "
                + "Похоже, трафик идёт мимо VPN. Если это твой же VPN на другом сервере — добавь провайдера в список.");
        }

        if (settings.TreatUnexpectedIPv4AsLeakRisk && expectedIpMismatch)
        {
            return Result(MonitorStatus.LeakRisk, "Внешний IPv4 не входит в список разрешенных IP.");
        }

        if (settings.TreatUnexpectedIPv4AsLeakRisk
            && baselineIpChanged
            && !settings.AllowIpChangesWithinExpectedCountry)
        {
            return Result(MonitorStatus.LeakRisk, "Внешний IPv4 изменился, а смена IP запрещена настройками.");
        }

        var rollingPing = rolling.AveragePingMs;
        if (rollingPing.HasValue && rollingPing.Value > settings.DegradedPingThresholdMs)
        {
            return Result(MonitorStatus.Degraded, $"Средний ping: {rollingPing.Value:0} ms.");
        }

        if (rolling.PacketLossPercent > settings.DegradedPacketLossThresholdPercent)
        {
            return Result(MonitorStatus.Degraded, $"Потери пакетов: {rolling.PacketLossPercent:0.#}%.");
        }

        if (countryMissing)
        {
            return Result(MonitorStatus.CountryUnknown, "Страну внешнего IPv4 не удалось определить, но риск по стране выключен.");
        }

        if (countryMismatch)
        {
            return Result(
                MonitorStatus.CountryChanged,
                $"Страна изменилась: ожидалось {CountryNames.ToDisplayName(settings.ExpectedCountry)}, сейчас {CountryNames.ToDisplayName(snapshot.Country)}. Риск по стране выключен.");
        }

        if (settings.TreatUnexpectedIPv4AsLeakRisk
            && baselineIpChanged
            && settings.AllowIpChangesWithinExpectedCountry)
        {
            var description = string.IsNullOrWhiteSpace(settings.ExpectedCountry)
                ? "Внешний IPv4 изменился относительно baseline."
                : "Внешний IPv4 изменился внутри ожидаемой страны.";

            return Result(MonitorStatus.IpChanged, description);
        }

        if (baselineIpChanged || expectedIpMismatch)
        {
            return Result(MonitorStatus.Ok, BuildInformationalOkDescription(settings, baselineIpChanged, expectedIpMismatch));
        }

        return Result(MonitorStatus.Ok, "Интернет, IPv4, страна и задержка в пределах настроек.");
    }

    /// <summary>
    /// Provider risk (T-325): the exit provider is not among the ones the user accepts. Deliberately silent
    /// in three cases — check off, no allow-list yet, or the geo answer carried no provider at all. A missing
    /// provider means "we don't know", and "we don't know" must never be reported as a leak.
    /// </summary>
    public static bool ProviderIsUnexpected(NetworkSnapshot snapshot, AppSettings settings)
    {
        if (!settings.TreatProviderChangeAsLeakRisk
            || settings.AllowedProviders.Count == 0
            || ProviderMatcher.IsUnknown(snapshot.Asn, snapshot.Provider))
        {
            return false;
        }

        return !settings.AllowedProviders.Any(allowed =>
            ProviderMatcher.IsSameProvider(allowed.Asn, allowed.Name, snapshot.Asn, snapshot.Provider));
    }

    private static HealthResult Result(MonitorStatus status, string description)
    {
        return new HealthResult
        {
            Status = status,
            Description = description
        };
    }

    private static bool CountryMatches(string expectedCountry, string? actualCountry)
    {
        if (string.IsNullOrWhiteSpace(expectedCountry))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actualCountry))
        {
            return false;
        }

        var expected = CountryNames.NormalizeCountryCode(expectedCountry);
        var actual = CountryNames.NormalizeCountryCode(actualCountry);

        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExpectedInterfaceName(AppSettings settings)
    {
        return !string.IsNullOrWhiteSpace(settings.ExpectedInterfaceName)
            ? settings.ExpectedInterfaceName
            : settings.Baseline?.InterfaceName ?? string.Empty;
    }

    // Пользователь действительно мониторит VPN (есть baseline-сессия или заданы ожидания).
    // Без этого «HTTP+IP недоступны при живом ping» — просто сбой проверки, а не падение VPN.
    private static bool VpnConfigured(AppSettings settings, string expectedInterfaceName)
    {
        return settings.Baseline is not null
            || !string.IsNullOrWhiteSpace(settings.ExpectedCountry)
            || !string.IsNullOrWhiteSpace(expectedInterfaceName)
            || settings.ExpectedPublicIPv4.Count > 0;
    }

    private static bool InterfaceMatches(string expectedInterfaceName, string? actualInterfaceName)
    {
        if (string.IsNullOrWhiteSpace(expectedInterfaceName))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actualInterfaceName)
            || string.Equals(actualInterfaceName, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return actualInterfaceName.Contains(expectedInterfaceName, StringComparison.OrdinalIgnoreCase)
            || expectedInterfaceName.Contains(actualInterfaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRouteMismatchDescription(
        string expectedInterfaceName,
        string? actualInterfaceName,
        string detail)
    {
        var actual = string.IsNullOrWhiteSpace(actualInterfaceName) ? "неизвестно" : actualInterfaceName;
        return $"Похоже, VPN-маршрут сменился: ожидался «{expectedInterfaceName}», сейчас «{actual}». {detail}";
    }

    private static string BuildInformationalOkDescription(
        AppSettings settings,
        bool baselineIpChanged,
        bool expectedIpMismatch)
    {
        var parts = new List<string> { "OK" };

        if (baselineIpChanged)
        {
            var ipChangeText = string.IsNullOrWhiteSpace(settings.ExpectedCountry)
                ? "IPv4 изменился относительно baseline"
                : "IPv4 изменился внутри ожидаемой страны";
            parts.Add(ipChangeText);
        }

        if (expectedIpMismatch)
        {
            parts.Add("IPv4 вне списка, но риск по IPv4 выключен");
        }

        return string.Join(" · ", parts) + ".";
    }
}
