using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Identity of the adapter the user expects the default route to sit on.
///
/// Settings used to store a single display string, "Alias (Description)", and every comparison was a
/// two-way substring test against that whole blob. Two things broke on it: a driver update that changes
/// only the description invalidated the match, and the string carried no way to tell which half was the
/// Windows connection name — the half firewall rules and Get-NetAdapter actually key on.
///
/// So the parts are stored separately and compared separately: alias OR description is a match. The
/// composite string stays in settings for display and for rolling back to an older build.
/// </summary>
public sealed record ExpectedInterfaceIdentity(string Alias, string Description, string Id)
{
    public static ExpectedInterfaceIdentity Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Alias) && string.IsNullOrWhiteSpace(Description);

    /// <summary>"Alias (Description)" — the shape shown in the UI and written to logs.</summary>
    public string Display => ExpectedInterface.BuildDisplay(Alias, Description);

    /// <summary>
    /// A live adapter is the expected one when either half matches. The GUID is only ever a bonus:
    /// wintun regenerates it in some setups, so a mismatch there must not veto a good name match.
    /// </summary>
    public bool Matches(string? alias, string? description, string? id = null)
    {
        if (IsEmpty)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(id)
            && string.Equals(Id, id, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Same(Alias, alias) || Same(Description, description);
    }

    public bool Matches(NetworkAdapterInfo adapter)
        => Matches(adapter.Name, adapter.Description);

    private static bool Same(string expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected)
            && !string.IsNullOrWhiteSpace(actual)
            && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
}

public static class ExpectedInterface
{
    /// <summary>Configured VPN adapters; an explicit list replaces the legacy single adapter.</summary>
    public static IReadOnlyList<ExpectedInterfaceIdentity> AllFromSettings(AppSettings settings)
    {
        if (settings.RouteMode == VpnRouteMode.NoSeparateAdapter)
        {
            return Array.Empty<ExpectedInterfaceIdentity>();
        }

        if (settings.AllowedVpnInterfaces.Count > 0)
        {
            return settings.AllowedVpnInterfaces
                .Select(SplitDisplay)
                .Select(parts => new ExpectedInterfaceIdentity(parts.Alias, parts.Description, string.Empty))
                .Where(identity => !identity.IsEmpty)
                .ToList();
        }

        var legacy = FromSettings(settings);
        return legacy.IsEmpty ? Array.Empty<ExpectedInterfaceIdentity>() : new[] { legacy };
    }

    /// <summary>
    /// Reads the expected adapter out of settings. Prefers the split fields; falls back to parsing the
    /// legacy composite string, then to the baseline — so a settings file written by an older build keeps
    /// working without a rewrite.
    /// </summary>
    public static ExpectedInterfaceIdentity FromSettings(AppSettings settings)
    {
        if (settings.RouteMode == VpnRouteMode.NoSeparateAdapter)
        {
            return ExpectedInterfaceIdentity.Empty;
        }

        var alias = settings.ExpectedInterfaceAlias?.Trim() ?? string.Empty;
        var description = settings.ExpectedInterfaceDescription?.Trim() ?? string.Empty;
        var id = settings.ExpectedInterfaceId?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(alias) || !string.IsNullOrWhiteSpace(description))
        {
            return new ExpectedInterfaceIdentity(alias, description, id);
        }

        var legacy = !string.IsNullOrWhiteSpace(settings.ExpectedInterfaceName)
            ? settings.ExpectedInterfaceName
            : settings.Baseline?.InterfaceName ?? string.Empty;

        var (parsedAlias, parsedDescription) = SplitDisplay(legacy);
        return new ExpectedInterfaceIdentity(parsedAlias, parsedDescription, id);
    }

    /// <summary>
    /// Splits "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)" into its two halves.
    /// Only a trailing "(...)" counts — an adapter named "Wi-Fi (2.4 GHz) adapter" has no trailing
    /// group and comes back whole as the alias, which is the safe reading.
    /// </summary>
    public static (string Alias, string Description) SplitDisplay(string? display)
    {
        var text = display?.Trim() ?? string.Empty;
        if (text.Length == 0 || string.Equals(text, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return (string.Empty, string.Empty);
        }

        if (!text.EndsWith(')'))
        {
            return (text, string.Empty);
        }

        var open = text.LastIndexOf('(');
        if (open <= 0)
        {
            return (text, string.Empty);
        }

        var alias = text[..open].TrimEnd();
        var description = text[(open + 1)..^1].Trim();

        return alias.Length == 0
            ? (text, string.Empty)
            : (alias, description);
    }

    public static string BuildDisplay(string? alias, string? description)
    {
        var left = alias?.Trim() ?? string.Empty;
        var right = description?.Trim() ?? string.Empty;

        if (left.Length == 0)
        {
            return right;
        }

        return right.Length == 0 || string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? left
            : $"{left} ({right})";
    }

    /// <summary>Compares the expected adapter against a live "Alias (Description)" string.</summary>
    public static bool MatchesDisplay(ExpectedInterfaceIdentity expected, string? actualDisplay)
    {
        if (expected.IsEmpty)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actualDisplay)
            || string.Equals(actualDisplay, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            // "Не знаем" никогда не должно читаться как «маршрут уехал».
            return true;
        }

        var (alias, description) = SplitDisplay(actualDisplay);
        return expected.Matches(alias, description);
    }
}
