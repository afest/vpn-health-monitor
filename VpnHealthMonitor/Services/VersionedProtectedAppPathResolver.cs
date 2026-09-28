using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Finds newer executables in versioned extension and CLI folders even while the saved
/// executable remains on disk. The firewall rule is tied to the saved path, not this identity.
/// </summary>
internal static class VersionedProtectedAppPathResolver
{
    internal static IReadOnlyDictionary<string, string> Resolve(IReadOnlyList<ProtectedApp> apps)
    {
        var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cli = apps.Select(app => app.Path).Where(CliPathResolver.IsCliVersionedPath).ToArray();
        var extensions = apps.Select(app => app.Path).Where(VsCodeExtensionPathResolver.IsVersionedExtensionPath).ToArray();

        foreach (var pair in CliPathResolver.ResolveMovedPaths(cli))
        {
            moved[pair.Key] = pair.Value;
        }
        foreach (var pair in VsCodeExtensionPathResolver.ResolveMovedPaths(extensions))
        {
            moved[pair.Key] = pair.Value;
        }
        return moved;
    }
}
