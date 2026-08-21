using System.Diagnostics;
using System.IO;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Display name of a protected executable, always derived from the file itself.
///
/// Self-updating programs bake the version into FileDescription ("Claude Code 2.1.235 (VS Code)"), so a
/// name captured once at add-time goes stale on the very next update and then advertises a version that
/// is no longer on disk. Nothing here is user-editable, which is what makes re-resolving safe.
/// </summary>
public static class ProtectedAppNaming
{
    public static string Resolve(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription.Trim();
            }
        }
        catch
        {
            // fall through to file name
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Re-resolves the name only when the file is actually there; a dead path keeps its last known name.</summary>
    public static string RefreshIfPossible(string path, string currentName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return currentName;
            }
        }
        catch
        {
            return currentName;
        }

        var resolved = Resolve(path);
        return string.IsNullOrWhiteSpace(resolved) ? currentName : resolved;
    }
}
