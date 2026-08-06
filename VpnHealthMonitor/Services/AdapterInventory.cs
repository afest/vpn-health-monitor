using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Reads the machine's network adapters (T-323). Primary source is Get-NetAdapter — it is the only
/// one that exposes PhysicalMediaType and the Virtual flag, and it works without elevation.
/// Hidden adapters (WAN miniports, Wi-Fi Direct, Teredo) are left out: Get-NetAdapter omits them
/// unless -IncludeHidden is passed, and they are not egress paths.
///
/// Fallback (module missing / policy): System.Net.NetworkInformation. It has no Virtual flag, so
/// classification there falls back to name heuristics only — deliberately reported via
/// <see cref="AdapterInventoryResult.IsFallback"/> so the UI can say the list is less reliable.
/// </summary>
public sealed class AdapterInventory
{
    // [Console]::OutputEncoding — обязательная строка: без неё powershell.exe отдаёт stdout в кодовой
    // странице консоли (866), и русские имена адаптеров («Беспроводная сеть») приходят мусором.
    // Правило с таким InterfaceAlias не создастся — то есть защиты не будет при зелёном интерфейсе.
    private const string InventoryScript = @"$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
    $out = Get-NetAdapter | ForEach-Object {
        [PSCustomObject]@{
            name = [string]$_.Name
            description = [string]$_.InterfaceDescription
            media = [string]$_.PhysicalMediaType
            virtual = [bool]$_.Virtual
            status = [string]$_.Status
        }
    }
    if ($null -eq $out) { '[]' } else { ConvertTo-Json -InputObject @($out) -Compress }
} catch {
    ConvertTo-Json -InputObject ([ordered]@{ inventoryError = $_.Exception.Message }) -Compress
}
";

    public async Task<AdapterInventoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        var adapters = await ReadViaPowerShellAsync(cancellationToken).ConfigureAwait(false);
        if (adapters is not null && adapters.Count > 0)
        {
            return new AdapterInventoryResult(adapters, false, null);
        }

        var fallback = ReadViaDotNet();
        return fallback.Count > 0
            ? new AdapterInventoryResult(fallback, true, "Get-NetAdapter недоступен — список собран без флага Virtual, проверь его внимательнее.")
            : new AdapterInventoryResult(Array.Empty<NetworkAdapterInfo>(), true, "Не удалось прочитать список сетевых адаптеров.");
    }

    private static async Task<IReadOnlyList<NetworkAdapterInfo>?> ReadViaPowerShellAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folder = Path.Combine(AppPaths.AppDataFolder, "killswitch");
            Directory.CreateDirectory(folder);
            var scriptPath = Path.Combine(folder, "adapter-inventory.ps1");
            await File.WriteAllTextAsync(scriptPath, InventoryScript, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return ParseInventory(await stdoutTask.ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static IReadOnlyList<NetworkAdapterInfo>? ParseInventory(string stdout)
    {
        var json = stdout.TrimStart((char)0xFEFF).Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // { "inventoryError": "..." } — or a single adapter serialised as an object.
                return root.TryGetProperty("inventoryError", out _)
                    ? null
                    : new List<NetworkAdapterInfo> { ReadAdapter(root) };
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var list = new List<NetworkAdapterInfo>();
            foreach (var element in root.EnumerateArray())
            {
                var adapter = ReadAdapter(element);
                if (!string.IsNullOrWhiteSpace(adapter.Name))
                {
                    list.Add(adapter);
                }
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NetworkAdapterInfo ReadAdapter(JsonElement element) => new()
    {
        Name = GetStr(element, "name"),
        Description = GetStr(element, "description"),
        PhysicalMediaType = GetStr(element, "media"),
        IsVirtual = GetBool(element, "virtual"),
        Status = GetStr(element, "status")
    };

    private static IReadOnlyList<NetworkAdapterInfo> ReadViaDotNet()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(item => new NetworkAdapterInfo
                {
                    Name = item.Name,
                    Description = item.Description,
                    PhysicalMediaType = item.NetworkInterfaceType.ToString(),
                    IsVirtual = false,
                    Status = item.OperationalStatus.ToString()
                })
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<NetworkAdapterInfo>();
        }
    }

    private static string GetStr(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop))
        {
            return string.Empty;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString() ?? string.Empty,
            JsonValueKind.Number => prop.GetRawText(),
            _ => string.Empty
        };
    }

    private static bool GetBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop))
        {
            return false;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(prop.GetString(), out var parsed) && parsed,
            _ => false
        };
    }
}

/// <param name="Adapters">Everything the machine reports, blockable or not.</param>
/// <param name="IsFallback">True when Get-NetAdapter was unavailable and the Virtual flag is unknown.</param>
/// <param name="Warning">Human-readable caveat to surface in the confirmation screen, if any.</param>
public sealed record AdapterInventoryResult(
    IReadOnlyList<NetworkAdapterInfo> Adapters,
    bool IsFallback,
    string? Warning);
