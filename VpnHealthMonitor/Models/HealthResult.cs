using VpnHealthMonitor.Services;

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

    /// <summary>Whether proxy/no-adapter mode has at least one signal that can notice direct ISP egress.</summary>
    public VpnExitCheckState ExitCheck { get; init; } = VpnExitCheckState.NotConfigured;
}

public enum RouteCheckState
{
    /// <summary>"Риск по маршруту VPN" is switched off in settings.</summary>
    Disabled,

    /// <summary>The selected VPN mode does not create a Windows adapter, so route comparison is intentionally skipped.</summary>
    NotApplicable,

    /// <summary>The separate-adapter mode is selected, but the saved adapter is empty, invalid or absent now.</summary>
    NeedsConfiguration,

    /// <summary>Switched on against a currently available VPN adapter — the check is really guarding the route.</summary>
    Active
}

public static class RouteCheckStateText
{
    public static string ToDisplayText(this RouteCheckState state) => state switch
    {
        RouteCheckState.Active => "Проверка маршрута активна",
        RouteCheckState.NeedsConfiguration => "Настройка маршрута устарела",
        RouteCheckState.NotApplicable => "Проверка маршрута не применима к твоему режиму VPN",
        _ => "Проверка маршрута выключена"
    };
}

public enum VpnExitCheckState
{
    NotConfigured,
    Configured
}

/// <summary>
/// Live adapter inventory supplied by the desktop UI. Tests and non-UI callers may use Unknown;
/// an unknown inventory never declares a previously working route stale by itself.
/// </summary>
public sealed record RouteCheckContext(bool InventoryAvailable, IReadOnlyCollection<string> AvailableInterfaces)
{
    public static RouteCheckContext Unknown { get; } = new(false, Array.Empty<string>());

    /// <summary>
    /// Adapters as read from Windows, with alias and description apart. When present, matching runs on
    /// these; the flat name list stays for callers that only ever had strings.
    /// </summary>
    public IReadOnlyCollection<NetworkAdapterInfo> Adapters { get; init; } = Array.Empty<NetworkAdapterInfo>();

    public static RouteCheckContext FromAdapters(IReadOnlyCollection<NetworkAdapterInfo> adapters)
        => new(
            true,
            adapters
                .SelectMany(adapter => new[] { adapter.Name, adapter.DisplayName })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList())
        {
            Adapters = adapters
        };

    public bool Contains(ExpectedInterfaceIdentity expected)
    {
        if (!InventoryAvailable || expected.IsEmpty)
        {
            return false;
        }

        if (Adapters.Count > 0)
        {
            return Adapters.Any(expected.Matches);
        }

        return Contains(expected.Display);
    }

    public bool Contains(string expectedInterfaceName)
    {
        if (!InventoryAvailable || string.IsNullOrWhiteSpace(expectedInterfaceName))
        {
            return false;
        }

        return AvailableInterfaces.Any(actual =>
            actual.Contains(expectedInterfaceName, StringComparison.OrdinalIgnoreCase)
            || expectedInterfaceName.Contains(actual, StringComparison.OrdinalIgnoreCase));
    }
}
