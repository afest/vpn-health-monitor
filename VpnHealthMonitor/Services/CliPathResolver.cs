using System.IO;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Resolves the current on-disk path of the Claude Code CLI executable, which self-updates into a
/// version-stamped folder: "...\Claude\claude-code\{version}\claude.exe"
/// (e.g. "...\AppData\Roaming\Claude\claude-code\2.1.181\claude.exe").
///
/// The version segment changes on every update, so a kill-switch rule pinned to the full path silently
/// points at a dead path after an update — the same failure <see cref="AppxPathResolver"/> prevents for
/// Store/MSIX apps, but the CLI uses a different, filesystem-only scheme. Given the now-missing old path,
/// we scan the stable "claude-code" parent for the newest version folder that still contains claude.exe and
/// re-point the rule there.
///
/// Read-only and non-elevated: plain directory enumeration, no PowerShell, no appx subsystem. Detection is
/// permissive (recognise the layout even for odd version strings); resolution is conservative (only follow a
/// folder whose name parses as an all-numeric dotted version and that actually contains the exe).
/// </summary>
public static class CliPathResolver
{
    private const string ExeName = "claude.exe";
    private const string CodeFolderName = "claude-code";

    /// <summary>True if the path is the versioned CLI layout "...\claude-code\{version}\claude.exe".</summary>
    public static bool IsCliVersionedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionDir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(versionDir) || !IsVersion(Path.GetFileName(versionDir)))
        {
            return false;
        }

        var codeDir = Path.GetDirectoryName(versionDir);
        return !string.IsNullOrEmpty(codeDir)
               && string.Equals(Path.GetFileName(codeDir), CodeFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stable identity across version updates: the lowercased "claude-code" folder path. Null when the path
    /// is not a versioned CLI path. Mirrors the family-name identity <see cref="AppxPathResolver"/> gives MSIX apps.
    /// </summary>
    public static string? GetStableKey(string? path)
    {
        if (!IsCliVersionedPath(path))
        {
            return null;
        }

        var codeDir = Path.GetDirectoryName(Path.GetDirectoryName(path!));
        return codeDir?.TrimEnd('\\').ToLowerInvariant();
    }

    /// <summary>
    /// For each given (now-missing) versioned CLI path, resolve the newest existing claude.exe under the same
    /// "claude-code" folder. Returns oldPath → newExePath only when a different, existing path was found.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveMovedPaths(IReadOnlyList<string> oldPaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldPath in oldPaths)
        {
            if (!IsCliVersionedPath(oldPath))
            {
                continue;
            }

            var codeDir = Path.GetDirectoryName(Path.GetDirectoryName(oldPath)!)!;

            string? newest;
            try
            {
                var candidates = Directory.EnumerateDirectories(codeDir)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name)
                                   && IsVersion(name)
                                   && SafeFileExists(Path.Combine(codeDir, name!, ExeName)))
                    .Select(name => name!)
                    .ToList();
                var best = MaxVersionName(candidates);
                newest = best is null ? null : Path.Combine(codeDir, best, ExeName);
            }
            catch
            {
                newest = null;
            }

            if (!string.IsNullOrEmpty(newest) && !PathEquals(newest!, oldPath) && SafeFileExists(newest!))
            {
                result[oldPath] = newest!;
            }
        }

        return result;
    }

    /// <summary>From a set of version folder names, return the one with the highest numeric version (null if none parse).</summary>
    internal static string? MaxVersionName(IEnumerable<string> versionNames)
    {
        string? bestName = null;
        int[]? best = null;
        foreach (var name in versionNames)
        {
            var parsed = ParseVersion(name);
            if (parsed is null)
            {
                continue;
            }

            if (best is null || CompareVersions(parsed, best) > 0)
            {
                best = parsed;
                bestName = name;
            }
        }

        return bestName;
    }

    /// <summary>A dot-separated, all-numeric version with at least two components, e.g. "2.1.181".</summary>
    internal static bool IsVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var parts = name.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                return false;
            }

            foreach (var ch in part)
            {
                if (ch < '0' || ch > '9')
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Parse an all-numeric dotted version into its components, or null if it does not parse (incl. overflow).</summary>
    internal static int[]? ParseVersion(string? name)
    {
        if (!IsVersion(name))
        {
            return null;
        }

        var parts = name!.Split('.');
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out nums[i]))
            {
                return null;
            }
        }

        return nums;
    }

    /// <summary>Element-wise version compare; shorter versions are zero-padded ("2.1" == "2.1.0").</summary>
    internal static int CompareVersions(int[] a, int[] b)
    {
        var length = Math.Max(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            var ai = i < a.Length ? a[i] : 0;
            var bi = i < b.Length ? b[i] : 0;
            if (ai != bi)
            {
                return ai.CompareTo(bi);
            }
        }

        return 0;
    }

    private static bool SafeFileExists(string path)
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
}
