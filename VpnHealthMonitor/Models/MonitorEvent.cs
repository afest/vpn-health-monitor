using System.Text.Json.Serialization;

namespace VpnHealthMonitor.Models;

public sealed class MonitorEvent
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    public MonitorStatus Status { get; set; } = MonitorStatus.Unknown;

    public string Description { get; set; } = string.Empty;

    public string? IPv4 { get; set; }

    public string? IPv6 { get; set; }

    public string? Country { get; set; }

    public string? Asn { get; set; }

    public string? Provider { get; set; }

    public double? PingAverageMs { get; set; }

    public double? PacketLossPercent { get; set; }

    public string? InterfaceName { get; set; }

    public string? DnsInfo { get; set; }

    /// <summary>Event family, e.g. "killswitch" for per-app kill switch events. Null for network events.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Category { get; set; }

    /// <summary>Kill switch action, e.g. "app_added", "rules_applied", "rule_verification_failed".</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Action { get; set; }

    /// <summary>Protected app display name for kill switch events.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppName { get; set; }

    /// <summary>Protected app .exe path for kill switch events.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppPath { get; set; }

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss");

    public string StatusText => Status.ToDisplayText();

    public string CountryText => CountryNames.ToDisplayName(Country);

    public string PingText => PingAverageMs.HasValue ? $"{PingAverageMs.Value:0} ms" : "н/д";

    public string PacketLossText => PacketLossPercent.HasValue ? $"{PacketLossPercent.Value:0.#}%" : "н/д";
}
