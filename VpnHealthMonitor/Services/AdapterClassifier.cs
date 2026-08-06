using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Decides which adapters the per-app kill switch must block direct egress through (T-323).
///
/// Previous model (T-179) whitelisted media types 802.3 / 802.11 / Bluetooth. Anything the driver
/// reports differently — most importantly built-in LTE/WWAN ("Wireless WAN") — silently fell out of
/// the block list while the UI still said "Защищено". The model is now inverted: block everything
/// that is not recognisably virtual or VPN.
///
/// Order matters, and it is deliberately biased towards NOT cutting the user off:
///   1. Name/description looks like a VPN or a virtual adapter  → never blocked. Blocking a VPN
///      adapter would kill the protected app even with the VPN up, which is worse than a gap the
///      confirmation screen can catch.
///   2. Adapter is real hardware (Virtual = false)              → blocked.
///   3. Adapter is virtual but sits on physical media (Bluetooth PAN is Virtual=true on Windows,
///      and it is a real egress path) → blocked.
///   4. Anything else (virtual + Unspecified media: TUN/TAP, Hyper-V, WSL) → not blocked.
///
/// The remaining gap — real hardware whose driver reports Virtual=true with Unspecified media —
/// is covered by showing the user the resulting list before the rules are applied.
/// </summary>
public static class AdapterClassifier
{
    /// <summary>Media types that carry real traffic off the machine. Matched as a substring, case-insensitive.</summary>
    private static readonly string[] PhysicalMediaHints =
    {
        "802.3",        // Ethernet
        "802.11",       // Wi-Fi ("Native 802.11")
        "bluetooth",    // Bluetooth PAN (tethering)
        "wireless wan", // built-in LTE/WWAN — the case T-316 found missing
        "wman",
        "wwan",
        "dsl",
        "cable modem",
        "phone line",
        "power line"
    };

    public static IReadOnlyList<NetworkAdapterInfo> SelectBlockable(IEnumerable<NetworkAdapterInfo> adapters)
        => adapters.Where(IsBlockable).ToList();

    public static IReadOnlyList<NetworkAdapterInfo> SelectExcluded(IEnumerable<NetworkAdapterInfo> adapters)
        => adapters.Where(adapter => !IsBlockable(adapter)).ToList();

    public static bool IsBlockable(NetworkAdapterInfo adapter)
    {
        if (adapter is null || string.IsNullOrWhiteSpace(adapter.Name))
        {
            return false;
        }

        if (VpnInterfaceHeuristics.LooksLikeVpn(adapter.Name)
            || VpnInterfaceHeuristics.LooksLikeVpn(adapter.Description)
            || VpnInterfaceHeuristics.LooksLikeVirtual(adapter.Name)
            || VpnInterfaceHeuristics.LooksLikeVirtual(adapter.Description))
        {
            return false;
        }

        if (!adapter.IsVirtual)
        {
            return true;
        }

        return IsPhysicalMedia(adapter.PhysicalMediaType);
    }

    private static bool IsPhysicalMedia(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return false;
        }

        return PhysicalMediaHints.Any(hint => mediaType.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }
}
