using System;
using System.IO;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Pure and self-contained filesystem tests for VS Code extension sidecar resolution. These
/// executables are security-critical for AI extensions: protecting Code.exe alone does not protect
/// extension-bundled native helpers such as OpenAI's codex.exe or Anthropic's claude.exe.
/// </summary>
public class VsCodeExtensionPathResolverTests
{
    [Theory]
    [InlineData(
        @"C:\Users\demo\.vscode\extensions\openai.chatgpt-26.623.101652-win32-x64\bin\windows-x86_64\codex.exe",
        true)]
    [InlineData(
        @"C:\Users\demo\.vscode\extensions\anthropic.claude-code-2.1.201-win32-x64\resources\native-binary\claude.exe",
        true)]
    [InlineData(
        @"C:\Users\demo\.vscode-insiders\extensions\anthropic.claude-code-2.1.201-win32-x64\resources\native-binary\claude.exe",
        true)]
    [InlineData(@"C:\Users\demo\.vscode\extensions\anthropic.claude-code\resources\native-binary\claude.exe", false)]
    [InlineData(@"C:\Users\demo\AppData\Roaming\Claude\claude-code\2.1.181\claude.exe", false)]
    [InlineData("")]
    [InlineData(null)]
    public void IsVersionedExtensionPath_DetectsVsCodeExtensionLayout(string? path, bool expected = false)
    {
        Assert.Equal(expected, VsCodeExtensionPathResolver.IsVersionedExtensionPath(path));
    }

    [Theory]
    [InlineData("openai.chatgpt-26.623.101652-win32-x64", "openai.chatgpt")]
    [InlineData("anthropic.claude-code-2.1.201-win32-x64", "anthropic.claude-code")]
    [InlineData("publisher.extension-name-with-hyphens-1.2.3", "publisher.extension-name-with-hyphens")]
    public void GetExtensionId_StripsVersionAndPlatformSuffix(string folder, string expected)
    {
        Assert.Equal(expected, VsCodeExtensionPathResolver.GetExtensionId(folder));
    }

    [Fact]
    public void TryParse_RealAnthropicPath_SplitsStableParts()
    {
        var ok = VsCodeExtensionPathResolver.TryParse(
            @"C:\Users\demo\.vscode\extensions\anthropic.claude-code-2.1.201-win32-x64\resources\native-binary\claude.exe",
            out var extensionsRoot,
            out var extensionFolder,
            out var extensionId,
            out var relativePath);

        Assert.True(ok);
        Assert.Equal(@"C:\Users\demo\.vscode\extensions", extensionsRoot);
        Assert.Equal("anthropic.claude-code-2.1.201-win32-x64", extensionFolder);
        Assert.Equal("anthropic.claude-code", extensionId);
        Assert.Equal(@"resources\native-binary\claude.exe", relativePath);
    }

    [Fact]
    public void GetStableKey_StableAcrossVersionBump()
    {
        var a = VsCodeExtensionPathResolver.GetStableKey(
            @"C:\Users\demo\.vscode\extensions\anthropic.claude-code-2.1.201-win32-x64\resources\native-binary\claude.exe");
        var b = VsCodeExtensionPathResolver.GetStableKey(
            @"C:\Users\demo\.vscode\extensions\anthropic.claude-code-2.1.202-win32-x64\resources\native-binary\claude.exe");

        Assert.Equal(a, b);
    }

    [Fact]
    public void ResolveMovedPaths_FollowsToHighestExistingExtensionVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "vhm-vscode-ext-" + Guid.NewGuid().ToString("N"));
        var extensions = Path.Combine(root, ".vscode", "extensions");
        var relative = Path.Combine("resources", "native-binary", "claude.exe");
        try
        {
            foreach (var folder in new[]
                     {
                         "anthropic.claude-code-2.1.9-win32-x64",
                         "anthropic.claude-code-2.1.201-win32-x64",
                         "anthropic.claude-code-2.1.42-win32-x64"
                     })
            {
                var exe = Path.Combine(extensions, folder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
                File.WriteAllText(exe, "stub");
            }

            // Higher version without the same sidecar must be ignored.
            Directory.CreateDirectory(Path.Combine(extensions, "anthropic.claude-code-2.2.0-win32-x64"));

            var oldMissing = Path.Combine(extensions, "anthropic.claude-code-2.1.1-win32-x64", relative);
            var moved = VsCodeExtensionPathResolver.ResolveMovedPaths(new[] { oldMissing });

            Assert.True(moved.ContainsKey(oldMissing));
            Assert.Equal(
                Path.Combine(extensions, "anthropic.claude-code-2.1.201-win32-x64", relative),
                moved[oldMissing]);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
