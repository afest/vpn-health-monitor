namespace VpnHealthMonitor.Models;

public sealed class NetworkSnapshot
{
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.Now;

    public string? ExternalIPv4 { get; init; }

    public string? ExternalIPv6 { get; init; }

    public string IPv6CheckStatus { get; init; } = "Выключено";

    public string? Country { get; init; }

    public string? Asn { get; init; }

    public string? Provider { get; init; }

    public string? InterfaceName { get; init; }

    public List<string> IpApiResults { get; init; } = new();

    public List<string> DnsServers { get; init; } = new();

    public bool HttpAvailable { get; init; }

    public double? PingAverageMs { get; init; }

    public double PacketLossPercent { get; init; } = 100;

    public bool PingAvailable { get; init; }

    public int PingAttempts { get; init; }

    public int PingSuccesses { get; init; }

    public List<string> Errors { get; init; } = new();

    public bool IpLookupSucceeded => !string.IsNullOrWhiteSpace(ExternalIPv4);
}
