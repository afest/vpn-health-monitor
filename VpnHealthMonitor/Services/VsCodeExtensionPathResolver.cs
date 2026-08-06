using System.IO;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Resolves executables bundled inside versioned VS Code extension folders, for example:
/// "...\Users\me\.vscode\extensions\anthropic.claude-code-2.1.201-win32-x64\resources\native-binary\claude.exe".
///
/// VS Code updates extensions by installing a sibling folder with a new version in its name. Firewall
/// rules are pinned to the full executable path, so a protected extension sidecar silently stops being
/// protected after the old folder is removed unless we follow the stable extension id.
/// </summary>
public static class VsCodeExtensionPathResolver
{
    private static readonly string[] ExtensionMarkers =
    {
        @"\.vscode\extensions\",
        @"\.vscode-insiders\extensions\"
    };

    public static bool IsVersionedExtensionPath(string? path)
        => TryParse(path, out _, out _, out _, out _);

    public static string? GetStableKey(string? path)
    {
        if (!TryParse(path, out var extensionsRoot, out _, out var extensionId, out _))
        {
            return null;
        }

        return (extensionsRoot.TrimEnd('\\') + "\\" + extensionId).ToLowerInvariant();
    }

    public static IReadOnlyDictionary<string, string> ResolveMovedPaths(IReadOnlyList<string> oldPaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldPath in oldPaths)
        {
            if (!TryParse(oldPath, out var extensionsRoot, out _, out var extensionId, out var relativePath))
            {
                continue;
            }

            string? newest = null;
            int[]? newestVersion = null;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(extensionsRoot))
                {
                    var folder = Path.GetFileName(dir);
                    if (!string.Equals(GetExtensionId(folder), extensionId, StringComparison.OrdinalIgnoreCase)
                        || TryParseVersion(folder, out var version) == false)
                    {
                        continue;
                    }

                    var candidate = Path.Combine(dir, relativePath);
                    if (!SafeFileExists(candidate))
                    {
                        continue;
                    }

                    if (newestVersion is null || CompareVersions(version, newestVersion) > 0)
                    {
                        newestVersion = version;
                        newest = candidate;
                    }
                }
            }
            catch
            {
                newest = null;
            }

            if (!string.IsNullOrWhiteSpace(newest) && !PathEquals(newest!, oldPath))
            {
                result[oldPath] = newest!;
            }
        }

        return result;
    }

    internal static bool TryParse(
        string? path,
        out string extensionsRoot,
        out string extensionFolder,
        out string extensionId,
        out string relativePath)
    {
        extensionsRoot = string.Empty;
        extensionFolder = string.Empty;
        extensionId = string.Empty;
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var marker in ExtensionMarkers)
        {
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                continue;
            }

            var tail = path[(idx + marker.Length)..];
            var slash = tail.IndexOf('\\');
            if (slash <= 0 || slash >= tail.Length - 1)
            {
                return false;
            }

            var folder = tail[..slash];
            var id = GetExtensionId(folder);
            if (id is null || TryParseVersion(folder, out _) == false)
            {
                return false;
            }

            extensionsRoot = path[..(idx + marker.Length - 1)];
            extensionFolder = folder;
            extensionId = id;
            relativePath = tail[(slash + 1)..];
            return true;
        }

        return false;
    }

    internal static string? GetExtensionId(string? extensionFolder)
    {
        if (string.IsNullOrWhiteSpace(extensionFolder))
        {
            return null;
        }

        for (var i = extensionFolder.LastIndexOf('-'); i > 0; i = extensionFolder.LastIndexOf('-', i - 1))
        {
            var suffix = extensionFolder[(i + 1)..];
            if (TryParseVersionPrefix(suffix, out _))
            {
                var id = extensionFolder[..i];
                return id.Contains('.', StringComparison.Ordinal) ? id : null;
            }
        }

        return null;
    }

    internal static bool TryParseVersion(string? extensionFolder, out int[] version)
    {
        version = Array.Empty<int>();
        var id = GetExtensionId(extensionFolder);
        if (id is null || extensionFolder!.Length <= id.Length + 1)
        {
            return false;
        }

        return TryParseVersionPrefix(extensionFolder[(id.Length + 1)..], out version);
    }

    private static bool TryParseVersionPrefix(string text, out int[] version)
    {
        version = Array.Empty<int>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var dash = text.IndexOf('-');
        var versionText = dash >= 0 ? text[..dash] : text;
        var parts = versionText.Split('.');
        if (parts.Length < 2)
        {
            return false;
        }

        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out parsed[i]))
            {
                return false;
            }
        }

        version = parsed;
        return true;
    }

    private static int CompareVersions(int[] a, int[] b)
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
