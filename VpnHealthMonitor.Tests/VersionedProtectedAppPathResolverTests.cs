using System;
using System.IO;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

public class VersionedProtectedAppPathResolverTests
{
    [Fact]
    public void Detects_new_extension_executable_while_old_file_still_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-path-watch-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(root, ".vscode", "extensions");
        var relative = Path.Combine("resources", "native-binary", "claude.exe");
        var oldPath = Path.Combine(extensions, "anthropic.claude-code-2.1.263-win32-x64", relative);
        var newPath = Path.Combine(extensions, "anthropic.claude-code-2.1.283-win32-x64", relative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.WriteAllText(oldPath, "old");
            File.WriteAllText(newPath, "new");

            var moves = VersionedProtectedAppPathResolver.Resolve(new[] { new ProtectedApp { Path = oldPath } });

            Assert.True(File.Exists(oldPath));
            Assert.Equal(newPath, moves[oldPath]);
            Assert.Empty(VersionedProtectedAppPathResolver.Resolve(new[] { new ProtectedApp { Path = newPath } }));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
