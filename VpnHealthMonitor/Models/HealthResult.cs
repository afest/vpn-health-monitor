namespace VpnHealthMonitor.Models;

public sealed class HealthResult
{
    public MonitorStatus Status { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Whether the "VPN route" check actually guards anything right now (T-323). The check compares the
    /// default-route interface with the expected one; in system-proxy mode the default route never moves,
    /// so the comparison can never fire and a silently green check would be a lie.
    /// </summary>
    public RouteCheckState RouteCheck { get; init; } = RouteCheckState.Disabled;
}

public enum RouteCheckState
{
    /// <summary>"Риск по маршруту VPN" is switched off in settings.</summary>
    Disabled,

    /// <summary>Switched on, but the expected interface is empty or does not look like a VPN adapter — the check cannot fire.</summary>
    NotApplicable,

    /// <summary>Switched on against a VPN-looking interface — the check is really guarding the route.</summary>
    Active
}

public static class RouteCheckStateText
{
    public static string ToDisplayText(this RouteCheckState state) => state switch
    {
        RouteCheckState.Active => "Проверка маршрута активна",
        RouteCheckState.NotApplicable => "Проверка маршрута не применима к твоему режиму VPN",
        _ => "Проверка маршрута выключена"
    };
}
