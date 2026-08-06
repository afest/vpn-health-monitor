using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

public sealed class LogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    public async Task WriteEventAsync(MonitorEvent monitorEvent, AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.AutosaveLogs)
        {
            return;
        }

        var path = EnsureLogPath(settings);
        var json = JsonSerializer.Serialize(monitorEvent, JsonOptions);
        await File.AppendAllTextAsync(path, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    public void WriteEvent(MonitorEvent monitorEvent, AppSettings settings)
    {
        if (!settings.AutosaveLogs)
        {
            return;
        }

        var path = EnsureLogPath(settings);
        var json = JsonSerializer.Serialize(monitorEvent, JsonOptions);
        File.AppendAllText(path, json + Environment.NewLine);
    }

    public string ExportCsv(AppSettings settings)
    {
        Directory.CreateDirectory(settings.LogsFolderPath);
        var exportPath = Path.Combine(settings.LogsFolderPath, $"vpn-health-monitor-export-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var inputFiles = Directory
            .GetFiles(settings.LogsFolderPath, "vpn-health-monitor-*.jsonl")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var writer = new StreamWriter(exportPath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("timestamp,status,ipv4,ipv6,country,asn_provider,ping_ms,packet_loss_percent,dns_info,interface_default_route,description");

        foreach (var inputFile in inputFiles)
        {
            foreach (var line in File.ReadLines(inputFile))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                MonitorEvent? monitorEvent;
                try
                {
                    monitorEvent = JsonSerializer.Deserialize<MonitorEvent>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (monitorEvent is null)
                {
                    continue;
                }

                var asnProvider = string.Join(" ", new[] { monitorEvent.Asn, monitorEvent.Provider }
                    .Where(item => !string.IsNullOrWhiteSpace(item)));
                var row = new[]
                {
                    monitorEvent.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    monitorEvent.Status.ToDisplayText(),
                    monitorEvent.IPv4,
                    monitorEvent.IPv6,
                    monitorEvent.Country,
                    asnProvider,
                    monitorEvent.PingAverageMs?.ToString("0.##", CultureInfo.InvariantCulture),
                    monitorEvent.PacketLossPercent?.ToString("0.##", CultureInfo.InvariantCulture),
                    monitorEvent.DnsInfo,
                    monitorEvent.InterfaceName,
                    monitorEvent.Description
                };

                writer.WriteLine(string.Join(",", row.Select(EscapeCsv)));
            }
        }

        return exportPath;
    }

    private static string EnsureLogPath(AppSettings settings)
    {
        Directory.CreateDirectory(settings.LogsFolderPath);
        return Path.Combine(settings.LogsFolderPath, $"vpn-health-monitor-{DateTime.Now:yyyy-MM-dd}.jsonl");
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return escaped.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? $"\"{escaped}\""
            : escaped;
    }
}
