namespace VpnHealthMonitor.Models;

/// <summary>
/// One network adapter as seen by Get-NetAdapter (or, in fallback mode, by
/// System.Net.NetworkInformation). Everything the kill-switch needs to decide whether
/// direct egress through this adapter must be blocked.
/// </summary>
public sealed class NetworkAdapterInfo
{
    /// <summary>Connection name / InterfaceAlias — the value firewall rules are bound to.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Driver-level description, e.g. "Intel(R) I211 Gigabit Network Connection".</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Get-NetAdapter PhysicalMediaType: "802.3", "Native 802.11", "BlueTooth", "Wireless WAN", "Unspecified"…</summary>
    public string PhysicalMediaType { get; init; } = string.Empty;

    /// <summary>Get-NetAdapter Virtual flag. Unknown (fallback source) is reported as false.</summary>
    public bool IsVirtual { get; init; }

    /// <summary>Get-NetAdapter Status ("Up", "Disconnected", "Not Present"). Informational only.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>"Имя (Описание)" — same shape the monitor uses for the default-route interface.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} ({Description})";
}
