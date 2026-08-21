using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

public static class HealthEvaluator
{
    /// <summary>
    /// Does the "VPN route" risk check actually guard anything (T-323/T-391)? Explicit no-adapter mode is
    /// not applicable. Separate-adapter mode is active only with a configured adapter that still exists in
    /// the live Windows inventory; a missing post-reinstall baseline needs configuration, not a leak verdict.
    /// </summary>
    public static RouteCheckState GetRouteCheckState(AppSettings settings, RouteCheckContext? context = null)
    {
        if (settings.RouteMode == VpnRouteMode.NoSeparateAdapter)
        {
            return RouteCheckState.NotApplicable;
        }

        if (!settings.TreatDefaultRouteChangeAsLeakRisk)
        {
            return RouteCheckState.Disabled;
        }

        var expected = ExpectedInterface.FromSettings(settings);
        if (expected.IsEmpty)
        {
            return RouteCheckState.NeedsConfiguration;
        }

        // A legacy settings file has no explicit mode. Preserve a real VPN-looking adapter, but never
        // reinterpret a physical Wi-Fi/Ethernet baseline as a useful tunnel check.
        if (settings.RouteMode is null && !VpnInterfaceHeuristics.LooksLikeVpn(expected.Display))
        {
            return RouteCheckState.NeedsConfiguration;
        }

        context ??= RouteCheckContext.Unknown;
        if (context.InventoryAvailable && !context.Contains(expected))
        {
            return RouteCheckState.NeedsConfiguration;
        }

        return RouteCheckState.Active;
    }

    public static HealthResult Evaluate(
        NetworkSnapshot snapshot,
        RollingHealthWindow rolling,
        AppSettings settings,
        RouteCheckContext? routeContext = null)
    {
        var routeCheck = GetRouteCheckState(settings, routeContext);
        var result = EvaluateStatus(snapshot, rolling, settings, routeCheck);
        return new HealthResult
        {
            Status = result.Status,
            Description = result.Description,
            RouteCheck = routeCheck,
            ExitCheck = GetExitCheckState(settings)
        };
    }

    public static VpnExitCheckState GetExitCheckState(AppSettings settings)
    {
        var countrySignal = settings.TreatCountryMismatchAsLeakRisk
            && !string.IsNullOrWhiteSpace(settings.ExpectedCountry);
        var providerSignal = settings.TreatProviderChangeAsLeakRisk
            && settings.AllowedProviders.Count > 0;
        var fixedIpSignal = settings.TreatUnexpectedIPv4AsLeakRisk
            && (settings.ExpectedPublicIPv4.Count > 0
                || (!settings.AllowIpChangesWithinExpectedCountry
                    && !string.IsNullOrWhiteSpace(settings.Baseline?.IPv4)));

        return countrySignal || providerSignal || fixedIpSignal
            ? VpnExitCheckState.Configured
            : VpnExitCheckState.NotConfigured;
    }

    private static HealthResult EvaluateStatus(
        NetworkSnapshot snapshot,
        RollingHealthWindow rolling,
        AppSettings settings,
        RouteCheckState routeCheck)
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
        var expectedInterface = ExpectedInterface.FromSettings(settings);
        var expectedInterfaceName = GetExpectedInterfaceName(settings);
        var defaultRouteMismatch = routeCheck == RouteCheckState.Active
            && !expectedInterface.IsEmpty
            && !ExpectedInterface.MatchesDisplay(expectedInterface, snapshot.InterfaceName);

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

        if (routeCheck == RouteCheckState.NeedsConfiguration
            && (settings.RouteMode is not null || !string.IsNullOrWhiteSpace(expectedInterfaceName)))
        {
            return Result(
                MonitorStatus.ConfigurationRequired,
                "Сохранённый VPN-интерфейс больше не найден в Windows. Выберите текущий режим VPN и, если он создаёт TUN/TAP/WireGuard, его адаптер.");
        }

        if (routeCheck == RouteCheckState.NotApplicable
            && GetExitCheckState(settings) == VpnExitCheckState.NotConfigured)
        {
            return Result(
                MonitorStatus.ConfigurationRequired,
                "Режим без отдельного адаптера выбран, но выход через VPN пока нечем отличить от прямого подключения. Настройте ожидаемую страну, провайдера/ASN или IPv4.");
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
        if (settings.RouteMode == VpnRouteMode.NoSeparateAdapter)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(settings.ExpectedInterfaceName))
        {
            return settings.ExpectedInterfaceName;
        }

        var display = ExpectedInterface.BuildDisplay(
            settings.ExpectedInterfaceAlias,
            settings.ExpectedInterfaceDescription);

        return !string.IsNullOrWhiteSpace(display)
            ? display
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
