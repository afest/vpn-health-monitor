using System;
using System.IO;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Приложение Codex раскладывает свои вспомогательные .exe по папкам с хэшем вместо номера версии и
/// при обновлении меняет хэш. Правило фаервола привязано к полному пути: монитор обязан найти новую
/// копию и показать «Путь устарел», а не «Файл не найден», пока новая копия ходит мимо VPN.
/// </summary>
public class CodexDesktopPathResolverTests
{
    private const string Bin = @"C:\Users\u\AppData\Local\OpenAI\Codex\bin\";
    private const string Swift = @"bin\node_modules\@oai\sky\bin\windows\swift\x64\codex-computer-use-swift.exe";

    [Theory]
    [InlineData(Bin + @"cdef5aaf3e41ab53\codex.exe")]
    [InlineData(Bin + @"f71a721e0e6193a9\codex-code-mode-host.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\OpenAI\Codex\runtimes\cua_node\4004642ff3fabdc7\" + Swift)]
    public void Recognises_hash_versioned_codex_paths(string path)
    {
        Assert.True(CodexDesktopPathResolver.IsVersionedPath(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.1103.4239.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe")]
    [InlineData(@"C:\Users\u\.codex\.sandbox-bin\codex.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\OpenAI\Codex\cdef5aaf3e41ab53\codex.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\OpenAI\Codex\bin\stable\codex.exe")]
    [InlineData(Bin + @"cdef5aaf3e41ab53\readme.txt")]
    public void Leaves_other_paths_alone(string path)
    {
        Assert.False(CodexDesktopPathResolver.IsVersionedPath(path));
        Assert.Null(CodexDesktopPathResolver.GetStableKey(path));
    }

    [Fact]
    public void Same_sidecar_keeps_its_identity_across_updates_and_differs_from_its_neighbours()
    {
        var oldCodex = ProtectedAppIdentity.ComputeKey(Bin + @"cdef5aaf3e41ab53\codex.exe");
        var newCodex = ProtectedAppIdentity.ComputeKey(Bin + @"f71a721e0e6193a9\codex.exe");
        var host = ProtectedAppIdentity.ComputeKey(Bin + @"f71a721e0e6193a9\codex-code-mode-host.exe");

        Assert.StartsWith("codex-desktop:", oldCodex);
        Assert.Equal(oldCodex, newCodex);
        Assert.NotEqual(newCodex, host);
    }

    [Fact]
    public void Finds_the_newest_copy_when_the_old_folder_is_gone()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-codex-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "OpenAI", "Codex", "bin");
        var oldPath = Path.Combine(bin, "cdef5aaf3e41ab53", "codex.exe");
        var olderSibling = Path.Combine(bin, "85e840bd9ad04c3b", "codex.exe");
        var newPath = Path.Combine(bin, "f71a721e0e6193a9", "codex.exe");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(olderSibling)!);
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.WriteAllText(olderSibling, "older");
            File.WriteAllText(newPath, "new");
            File.SetLastWriteTimeUtc(olderSibling, new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newPath, new DateTime(2026, 11, 9, 14, 36, 0, DateTimeKind.Utc));

            var moves = VersionedProtectedAppPathResolver.Resolve(new[] { new ProtectedApp { Path = oldPath } });

            Assert.Equal(newPath, moves[oldPath]);
            Assert.Empty(VersionedProtectedAppPathResolver.Resolve(new[] { new ProtectedApp { Path = newPath } }));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Follows_the_runtime_sidecar_into_the_new_runtime_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-codex-" + Guid.NewGuid().ToString("N"));
        var runtimes = Path.Combine(root, "OpenAI", "Codex", "runtimes", "cua_node");
        var oldPath = Path.Combine(runtimes, "4004642ff3fabdc7", Swift);
        var newPath = Path.Combine(runtimes, "9c26d707817e90a7", Swift);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            File.WriteAllText(newPath, "new");

            var moves = CodexDesktopPathResolver.ResolveMovedPaths(new[] { oldPath });

            Assert.Equal(newPath, moves[oldPath]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Old_codex_version_waits_for_the_desktop_app_restart()
    {
        // Главный процесс приложения Codex — ChatGPT.exe: запущенный до обновления может снова поднять
        // вспомогательную программу из старой папки, поэтому её правило снимать рано.
        Assert.Equal("ChatGPT", PreviousVersionUsage.HostProcessName(Bin + @"f71a721e0e6193a9\codex.exe"));
        Assert.Equal("Code", PreviousVersionUsage.HostProcessName(
            @"C:\Users\u\.vscode\extensions\openai.chatgpt-26.11.2-win32-x64\bin\windows-x86_64\codex.exe"));
    }
}
