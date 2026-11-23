using System.IO;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Executables of the Codex desktop app that live in hash-named version folders, for example:
/// "...\AppData\Local\OpenAI\Codex\bin\f71a721e0e6193a9\codex.exe" or
/// "...\OpenAI\Codex\runtimes\cua_node\9c26d707817e90a7\bin\node_modules\@oai\sky\...\codex-computer-use-swift.exe".
///
/// On every update Codex unpacks its sidecars into a new folder named by a content hash and removes the
/// old one. The folder name carries no version number, so the newest copy is the one whose executable was
/// written last. Firewall rules are pinned to the full path: without following the folder, the protected
/// sidecar turns into "file not found" while its new copy goes online past the VPN.
/// </summary>
public static class CodexDesktopPathResolver
{
    private const string Marker = @"\OpenAI\Codex\";

    public static bool IsVersionedPath(string? path) => TryParse(path, out _, out _, out _);

    /// <summary>Same for every version of one sidecar: the path with the hash folder replaced by "*".</summary>
    public static string? GetStableKey(string? path)
    {
        if (!TryParse(path, out var parent, out _, out var relativePath))
        {
            return null;
        }

        return (parent.TrimEnd('\\') + @"\*\" + relativePath).ToLowerInvariant();
    }

    /// <summary>Directory of the version folder that holds this sidecar; null for other paths.</summary>
    public static string? GetVersionFolder(string? path)
        => TryParse(path, out var parent, out var hashFolder, out _) ? Path.Combine(parent, hashFolder) : null;

    public static IReadOnlyDictionary<string, string> ResolveMovedPaths(IReadOnlyList<string> oldPaths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var oldPath in oldPaths)
        {
            if (!TryParse(oldPath, out var parent, out _, out var relativePath))
            {
                continue;
            }

            string? newest = null;
            var newestWrite = DateTime.MinValue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(parent))
                {
                    if (!IsHashFolder(Path.GetFileName(dir)))
                    {
                        continue;
                    }

                    var candidate = Path.Combine(dir, relativePath);
                    if (!SafeFileExists(candidate))
                    {
                        continue;
                    }

                    var written = File.GetLastWriteTimeUtc(candidate);
                    if (newest is null || written > newestWrite)
                    {
                        newest = candidate;
                        newestWrite = written;
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

    /// <summary>
    /// Splits a Codex desktop path into the folder that holds the version folders ("...\Codex\bin" or
    /// "...\Codex\runtimes\cua_node"), the hash folder itself and the path inside it. The hash folder is
    /// the first segment after "\OpenAI\Codex\" made only of hex digits.
    /// </summary>
    internal static bool TryParse(string? path, out string parent, out string hashFolder, out string relativePath)
    {
        parent = string.Empty;
        hashFolder = string.Empty;
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var idx = path.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return false;
        }

        var root = path[..(idx + Marker.Length - 1)];
        var segments = path[(idx + Marker.Length)..].Split('\\');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!IsHashFolder(segments[i]))
            {
                continue;
            }

            // Version folders sit at least one level deep ("bin", "runtimes\cua_node"): a hash right under
            // the Codex root would be something else.
            if (i == 0)
            {
                return false;
            }

            parent = Path.Combine(new[] { root }.Concat(segments[..i]).ToArray());
            hashFolder = segments[i];
            relativePath = string.Join('\\', segments[(i + 1)..]);
            return relativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    internal static bool IsHashFolder(string? name)
        => !string.IsNullOrEmpty(name)
           && name.Length is >= 8 and <= 64
           && name.All(Uri.IsHexDigit);

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
