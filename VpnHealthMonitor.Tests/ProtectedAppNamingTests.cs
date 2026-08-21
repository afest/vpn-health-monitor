using System.IO;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Naming has one job the path cannot do: let a person tell two rows apart. The sidecar shipped inside a
/// VS Code extension and the standalone CLI of the same product both report "Claude Code", so the sidecar
/// carries a suffix. Names are never user-editable, which is what makes re-resolving them safe.
/// </summary>
public class ProtectedAppNamingTests
{
    private static string MakeExe(string root, string relativeFolder, string file)
    {
        var folder = Path.Combine(root, relativeFolder);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, file);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void SidecarGetsTheExtensionSuffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-naming-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = MakeExe(root,
                Path.Combine(".vscode", "extensions", "anthropic.claude-code-2.1.238-win32-x64", "resources", "native-binary"),
                "claude.exe");

            Assert.Equal("claude.exe" + ProtectedAppNaming.VsCodeExtensionSuffix, ProtectedAppNaming.Resolve(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StandaloneCliKeepsThePlainName()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-naming-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = MakeExe(root, Path.Combine("npm", "bin"), "claude.exe");

            Assert.Equal("claude.exe", ProtectedAppNaming.Resolve(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SuffixIsNotAppendedTwice()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-naming-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = MakeExe(root,
                Path.Combine(".vscode", "extensions", "anthropic.claude-code-2.1.238-win32-x64", "bin"),
                "claude.exe");

            var once = ProtectedAppNaming.Resolve(path);
            var twice = ProtectedAppNaming.RefreshIfPossible(path, once);

            Assert.Equal(once, twice);
            Assert.Equal(1, twice.Split(ProtectedAppNaming.VsCodeExtensionSuffix).Length - 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SidecarAndCliEndUpWithDifferentNames()
    {
        // Обе строки живут в списке одновременно и обязаны различаться на глаз.
        var root = Path.Combine(Path.GetTempPath(), "vhm-naming-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sidecar = MakeExe(root,
                Path.Combine(".vscode", "extensions", "anthropic.claude-code-2.1.238-win32-x64", "resources", "native-binary"),
                "claude.exe");
            var cli = MakeExe(root, Path.Combine("npm", "node_modules", "@anthropic-ai", "claude-code", "bin"), "claude.exe");

            var settings = new AppSettings
            {
                ProtectedApps =
                {
                    new ProtectedApp { Name = "стало неактуальным", Path = sidecar },
                    new ProtectedApp { Name = "стало неактуальным", Path = cli }
                }
            };

            SettingsService.MigrateProtectedAppIdentity(settings);

            Assert.Equal(2, settings.ProtectedApps.Count);
            Assert.NotEqual(settings.ProtectedApps[0].Name, settings.ProtectedApps[1].Name);
            Assert.EndsWith(ProtectedAppNaming.VsCodeExtensionSuffix, settings.ProtectedApps[0].Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NameOfAMissingFileIsLeftAlone()
    {
        var settings = new AppSettings
        {
            ProtectedApps = { new ProtectedApp { Name = "Мой особый ярлык", Path = @"C:\no-such\ghost.exe" } }
        };

        SettingsService.MigrateProtectedAppIdentity(settings);

        Assert.Equal("Мой особый ярлык", Assert.Single(settings.ProtectedApps).Name);
    }
}
