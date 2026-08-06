namespace VpnHealthMonitor.Services;

/// <summary>
/// Single source of truth for "does this interface name look like a VPN / a virtual adapter".
/// Used by three callers that must agree (T-323): the route-check applicability status, the
/// expected-interface backfill, and the kill-switch adapter classifier. They used to carry two
/// divergent lists — one in MainWindow, one inside the elevated PowerShell helper.
/// </summary>
public static class VpnInterfaceHeuristics
{
    /// <summary>Substrings that mark a tunnel/VPN adapter. "tun" also matches the common "… Tunnel" suffix.</summary>
    private static readonly string[] VpnHints =
    {
        "vpn", "tun", "tap", "wintun", "wireguard", "openvpn", "karing", "clash",
        "sing-box", "nordlynx", "mullvad", "proton", "outline", "amnezia", "warp", "zerotier", "tailscale",
        // Corporate clients: they install an Ethernet-looking adapter, and blocking one would cut the
        // protected app off even with the tunnel up.
        "anyconnect", "secure mobility", "fortinet", "forticlient", "globalprotect", "global protect",
        "pulse secure", "zscaler", "netskope", "sonicwall", "check point", "checkpoint"
    };

    /// <summary>Substrings that mark a virtual / pseudo adapter that is not a real egress path.</summary>
    private static readonly string[] VirtualHints =
    {
        "loopback", "vmware", "hyper-v", "vethernet", "vswitch", "virtualbox", "vbox",
        "teredo", "isatap", "6to4", "wan miniport", "wi-fi direct", "virtual",
        "kernel debug", "ip-https", "pseudo-interface"
    };

    public static bool LooksLikeVpn(string? interfaceName)
        => ContainsAny(interfaceName, VpnHints);

    public static bool LooksLikeVirtual(string? interfaceName)
        => ContainsAny(interfaceName, VirtualHints);

    private static bool ContainsAny(string? value, string[] hints)
    {
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(value, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return hints.Any(hint => value.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }
}
