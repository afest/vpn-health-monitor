using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Resolves the current on-disk path of a Store/MSIX-packaged executable.
///
/// Packaged apps live under "...\WindowsApps\{Name}_{Version}_{Arch}__{PublisherId}\...\app.exe".
/// The version segment changes on every (auto-)update, so a kill-switch rule pinned to the full
/// path silently points at a dead path after an update. The package *family name*
/// ({Name}_{PublisherId}) is stable across versions, so we derive it from the old path, ask the
/// appx subsystem for the package's current InstallLocation, and re-attach the same in-package
/// relative exe path.
///
/// Read-only and non-elevated: Get-AppxPackage returns the current user's packages without UAC.
/// </summary>
public sealed class AppxPathResolver
{
    /// <summary>True if the path lives under a WindowsApps package folder.</summary>
    public static bool IsPackagedPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Split a packaged exe path into the package-full-name folder and the in-package relative exe
    /// path. e.g. "...\WindowsApps\Claude_1.2_x64__abc\app\claude.exe"
    /// → fullNameFolder="Claude_1.2_x64__abc", relativeExePath="app\claude.exe".
    /// </summary>
    public static bool TryParse(string path, out string fullNameFolder, out string relativeExePath)
    {
        fullNameFolder = string.Empty;
        relativeExePath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        const string marker = @"\WindowsApps\";
        var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var tail = path[(idx + marker.Length)..];
        var slash = tail.IndexOf('\\');
        if (slash <= 0 || slash >= tail.Length - 1)
        {
            return false;
        }

        fullNameFolder = tail[..slash];
        relativeExePath = tail[(slash + 1)..];
        return true;
    }

    /// <summary>
    /// Derive the stable package family name ({Name}_{PublisherId}) from a package full-name folder
    /// ({Name}_{Version}_{Arch}__{PublisherId}). The full-name format uses '_' as the separator and a
    /// package Name never contains '_', so the first and last non-empty segments are Name and
    /// PublisherId. (Validated against the real Claude package: "Claude_1.11187.4.0_x64__pzs8sxrjxfjjc"
    /// → "Claude_pzs8sxrjxfjjc".)
    /// </summary>
    public static string? GetFamilyName(string fullNameFolder)
    {
        if (string.IsNullOrWhiteSpace(fullNameFolder))
        {
            return null;
        }

        var parts = fullNameFolder.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        return parts[0] + "_" + parts[^1];
    }

    /// <summary>
    /// For each given (now-missing) packaged exe path, resolve the package's current exe path.
    /// Returns a map oldPath → newExePath only for packages found at a new, existing, different path.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ResolveMovedPathsAsync(
        IReadOnlyList<string> oldPaths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (oldPaths.Count == 0)
        {
            return result;
        }

        var installLocations = await QueryInstallLocationsAsync(cancellationToken).ConfigureAwait(false);
        if (installLocations is null || installLocations.Count == 0)
        {
            return result;
        }

        foreach (var oldPath in oldPaths)
        {
            if (!TryParse(oldPath, out var folder, out var relativeExePath))
            {
                continue;
            }

            var family = GetFamilyName(folder);
            if (family is null
                || !installLocations.TryGetValue(family, out var installLocation)
                || string.IsNullOrWhiteSpace(installLocation))
            {
                continue;
            }

            string newPath;
            try { newPath = Path.Combine(installLocation, relativeExePath); }
            catch { continue; }

            if (PathExists(newPath) && !PathEquals(newPath, oldPath))
            {
                result[oldPath] = newPath;
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<string, string>?> QueryInstallLocationsAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(KillSwitchFolder);
        var script = Path.Combine(KillSwitchFolder, "appx-query.ps1");
        await File.WriteAllTextAsync(script, AppxQueryScript, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            return ParseInstallLocations(stdout);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? ParseInstallLocations(string stdout)
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

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("queryError", out _))
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                {
                    AddEntry(map, el);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                AddEntry(map, root);
            }

            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AddEntry(Dictionary<string, string> map, JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var family = el.TryGetProperty("family", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        var location = el.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        if (!string.IsNullOrWhiteSpace(family) && !string.IsNullOrWhiteSpace(location))
        {
            map[family!] = location!;
        }
    }

    private static bool PathExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string KillSwitchFolder => Path.Combine(AppPaths.AppDataFolder, "killswitch");

    private const string AppxQueryScript = @"$ErrorActionPreference = 'Stop'
try {
    $out = Get-AppxPackage | Where-Object { $_.InstallLocation } | ForEach-Object {
        [PSCustomObject]@{ family = [string]$_.PackageFamilyName; location = [string]$_.InstallLocation }
    }
    if ($null -eq $out) { '[]' } else { ConvertTo-Json -InputObject @($out) -Compress }
} catch {
    ConvertTo-Json -InputObject ([ordered]@{ queryError = $_.Exception.Message }) -Compress
}
";
}
