namespace VpnHealthMonitor.Models;

public sealed class AppSettings
{
    /// <summary>Интервал цикла мониторинга. Внешний IP опрашивается каждый цикл (1 запрос ротацией) — быстрый сигнал смены сети/VPN; лимитированные geo-API щадятся через кэш по IP.</summary>
    public int IntervalSeconds { get; set; } = 5;

    public string ExpectedCountry { get; set; } = string.Empty;

    public bool TreatCountryMismatchAsLeakRisk { get; set; } = true;

    public List<string> ExpectedPublicIPv4 { get; set; } = new();

    public bool TreatUnexpectedIPv4AsLeakRisk { get; set; } = false;

    public bool AllowIpChangesWithinExpectedCountry { get; set; } = true;

    public string ExpectedInterfaceName { get; set; } = string.Empty;

    public bool TreatDefaultRouteChangeAsLeakRisk { get; set; } = true;

    public bool EnableIPv6LeakCheck { get; set; }

    public bool AllowExternalIPv6 { get; set; } = true;

    public List<string> IPv6ApiEndpoints { get; set; } = new()
    {
        "https://api6.ipify.org?format=json",
        "https://ifconfig.co/ip",
        "https://icanhazip.com"
    };

    /// <summary>
    /// Risk when the exit provider is not one of <see cref="AllowedProviders"/> (T-325). The only check that
    /// still works when the VPN exits in the user's own country: neither the country nor (in proxy mode) the
    /// default route changes when the tunnel drops, but the provider goes back to the home ISP.
    /// Off by default: a VPN that rotates exits between hosting companies would otherwise cry wolf until
    /// the user has taught it the full list, and a check that cries wolf gets switched off entirely.
    /// </summary>
    public bool TreatProviderChangeAsLeakRisk { get; set; }

    /// <summary>Providers considered "still my VPN". Filled by the baseline button and by hand.</summary>
    public List<ProviderIdentity> AllowedProviders { get; set; } = new();

    /// <summary>HTTP endpoints used only to answer "is the internet reachable at all".</summary>
    public List<string> HttpProbeUrls { get; set; } = new()
    {
        "https://www.cloudflare.com/cdn-cgi/trace",
        "https://www.google.com/generate_204",
        "https://www.msftconnecttest.com/connecttest.txt"
    };

    /// <summary>ICMP targets for latency and packet loss.</summary>
    public List<string> PingHosts { get; set; } = new() { "1.1.1.1", "8.8.8.8" };

    public bool EnableDnsCheck { get; set; } = true;

    public bool EnableWindowsNotifications { get; set; } = true;

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public List<string> IpApiEndpoints { get; set; } = new()
    {
        "https://api.ipify.org?format=json",
        "https://ifconfig.me/ip",
        "https://ipinfo.io/json",
        "https://ipapi.co/json/",
        "https://ipwho.is/"
    };

    public int DegradedPingThresholdMs { get; set; } = 250;

    public double DegradedPacketLossThresholdPercent { get; set; } = 5;

    public string LogsFolderPath { get; set; } = string.Empty;

    public bool AutosaveLogs { get; set; } = true;

    public BaselineInfo? Baseline { get; set; }

    /// <summary>Per-app kill switch: executables protected with Block-outbound-on-physical rules. Default empty.</summary>
    public List<ProtectedApp> ProtectedApps { get; set; } = new();

    /// <summary>
    /// Adapter names the user last confirmed as "block direct egress here" (T-323). Kept so the app can
    /// show what protection is actually bound to, and re-ask when the set of adapters changes.
    /// </summary>
    public List<string> ConfirmedBlockedAdapters { get; set; } = new();

    /// <summary>
    /// All adapter names present when the user confirmed the selection above, sorted and joined by "|".
    /// Compared on later runs so a manual tweak (unchecking a corporate VPN adapter that looks like plain
    /// Ethernet) survives, and the confirmation screen only comes back when the machine's hardware changed.
    /// </summary>
    public string ConfirmedAdapterFingerprint { get; set; } = string.Empty;

    // --- Per-type доставка балун-уведомлений (T-201) ---
    // Меняют только показ балуна, НЕ детект: статус всё равно вычисляется и виден в окне/tray-иконке.
    // Обратная совместимость: старый settings.json без этих полей сохраняет значение инициализатора
    // (System.Text.Json не трогает отсутствующие свойства) — Country=on, Ip=off.

    /// <summary>Балун при смене страны (CountryChanged) — ban-релевантный сигнал, по умолчанию включён.</summary>
    public bool NotifyCountryChanged { get; set; } = true;

    /// <summary>Балун при смене только внешнего IP (IpChanged) — шумный, по умолчанию выключен.</summary>
    public bool NotifyIpChanged { get; set; } = false;

    /// <summary>
    /// Доставлять ли балун для статуса. Гейтятся только «шумные» статусы под свой toggle;
    /// safety-сигналы (LeakRisk, NoInternet, VpnDown и пр.) доставляются всегда.
    /// Расширение: добавить bool-поле выше и кейс сюда.
    /// </summary>
    public bool ShouldNotifyForStatus(MonitorStatus status) => status switch
    {
        MonitorStatus.IpChanged => NotifyIpChanged,
        MonitorStatus.CountryChanged => NotifyCountryChanged,
        _ => true
    };
}

/// <summary>A provider the user accepts as their VPN exit: ASN, organisation name, or both.</summary>
public sealed class ProviderIdentity
{
    public string? Asn { get; set; }

    public string? Name { get; set; }
}

public sealed class BaselineInfo
{
    public string? IPv4 { get; set; }

    public string? Country { get; set; }

    public string? Provider { get; set; }

    public string? Asn { get; set; }

    public string? InterfaceName { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}
