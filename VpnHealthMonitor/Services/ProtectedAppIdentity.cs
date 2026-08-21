using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Identity of a protected app that survives its own updates.
///
/// The full exe path cannot play that role: Store packages, the Claude Code CLI and VS Code extension
/// sidecars all install to a new versioned folder on every update. Keying the list by path produced a
/// second entry per update — two rows in the UI, one real firewall rule, and a name frozen at whatever
/// version was current when the row was first added.
/// </summary>
public static class ProtectedAppIdentity
{
    public static string ComputeKey(string? path)
    {
        var value = path ?? string.Empty;

        if (AppxPathResolver.IsPackagedPath(value)
            && AppxPathResolver.TryParse(value, out var folder, out _)
            && AppxPathResolver.GetFamilyName(folder) is { } family)
        {
            return "appx:" + family;
        }

        if (CliPathResolver.GetStableKey(value) is { } cliKey)
        {
            return "cli:" + cliKey;
        }

        if (VsCodeExtensionPathResolver.GetStableKey(value) is { } vscodeExtensionKey)
        {
            return "vscode-ext:" + vscodeExtensionKey;
        }

        return "path:" + value.ToLowerInvariant();
    }

    public static string KeyOf(ProtectedApp app)
        => string.IsNullOrWhiteSpace(app.IdentityKey) ? ComputeKey(app.Path) : app.IdentityKey;
}
