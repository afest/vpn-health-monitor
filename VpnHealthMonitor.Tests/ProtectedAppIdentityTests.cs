using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// A protected app is identified by something that survives its own updates, not by the exe path.
/// The regression these cover: every Claude Code extension update added a second row for the same
/// binary, with a name frozen at the version that was current when the row was created.
/// </summary>
public class ProtectedAppIdentityTests
{
    private const string Ext238 =
        @"C:\Users\1\.vscode\extensions\anthropic.claude-code-2.1.238-win32-x64\resources\native-binary\claude.exe";

    private const string Ext235 =
        @"C:\Users\1\.vscode\extensions\anthropic.claude-code-2.1.235-win32-x64\resources\native-binary\claude.exe";

    [Fact]
    public void SameExtension_DifferentVersions_ShareOneKey()
    {
        Assert.Equal(ProtectedAppIdentity.ComputeKey(Ext235), ProtectedAppIdentity.ComputeKey(Ext238));
    }

    [Fact]
    public void ExtensionKey_IsNotJustThePath()
    {
        Assert.StartsWith("vscode-ext:", ProtectedAppIdentity.ComputeKey(Ext238));
    }

    [Fact]
    public void DifferentExtensions_DoNotCollide()
    {
        const string other =
            @"C:\Users\1\.vscode\extensions\vendor.other-tool-1.0.0-win32-x64\resources\native-binary\tool.exe";

        Assert.NotEqual(ProtectedAppIdentity.ComputeKey(other), ProtectedAppIdentity.ComputeKey(Ext238));
    }

    [Fact]
    public void PlainExe_FallsBackToPath()
    {
        Assert.Equal(@"path:d:\programs\node\node.exe", ProtectedAppIdentity.ComputeKey(@"D:\Programs\Node\node.exe"));
    }

    [Fact]
    public void KeyOf_PrefersTheStoredKey()
    {
        var app = new ProtectedApp { Path = Ext238, IdentityKey = "vscode-ext:pinned" };

        Assert.Equal("vscode-ext:pinned", ProtectedAppIdentity.KeyOf(app));
    }

    [Fact]
    public void Migration_CollapsesDuplicateExtensionRows()
    {
        // Exactly the shape found in the live settings file: two rows, same binary, stale names.
        var settings = new AppSettings
        {
            ProtectedApps =
            {
                new ProtectedApp
                {
                    Name = "Claude Code 2.1.235 (VS Code)",
                    Path = Ext238,
                    RuleName = "VPN Health Monitor - Block Direct - claude.exe [479a1bb0]",
                    AddedAt = new DateTimeOffset(2026, 7, 4, 10, 28, 0, TimeSpan.FromHours(5)),
                    RulesAppliedAt = new DateTimeOffset(2026, 8, 21, 11, 3, 37, TimeSpan.FromHours(5))
                },
                new ProtectedApp
                {
                    Name = "Claude Code 2.1.237 (VS Code)",
                    Path = Ext238,
                    RuleName = "VPN Health Monitor - Block Direct - claude.exe [479a1bb0]",
                    AddedAt = new DateTimeOffset(2026, 8, 20, 8, 35, 0, TimeSpan.FromHours(5)),
                    RulesAppliedAt = new DateTimeOffset(2026, 8, 21, 11, 3, 47, TimeSpan.FromHours(5))
                }
            }
        };

        SettingsService.MigrateProtectedAppIdentity(settings);

        var app = Assert.Single(settings.ProtectedApps);
        Assert.Equal(Ext238, app.Path);
        Assert.StartsWith("vscode-ext:", app.IdentityKey);
        // Самая ранняя установка защиты и самое свежее применение правил переживают склейку.
        Assert.Equal(new DateTimeOffset(2026, 7, 4, 10, 28, 0, TimeSpan.FromHours(5)), app.AddedAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 11, 3, 47, TimeSpan.FromHours(5)), app.RulesAppliedAt);
    }

    [Fact]
    public void Migration_KeepsDistinctApps()
    {
        var settings = new AppSettings
        {
            ProtectedApps =
            {
                new ProtectedApp { Name = "Code", Path = @"D:\Programs\VS Code\Code.exe" },
                new ProtectedApp { Name = "Node", Path = @"D:\Programs\Node\node.exe" },
                new ProtectedApp { Name = "Claude", Path = Ext238 }
            }
        };

        SettingsService.MigrateProtectedAppIdentity(settings);

        Assert.Equal(3, settings.ProtectedApps.Count);
        Assert.All(settings.ProtectedApps, app => Assert.NotEmpty(app.IdentityKey));
    }

    [Fact]
    public void Migration_DoesNotDropAnAppWhoseFileIsGone()
    {
        // A dead path may just be an unmounted drive. Silently dropping protection is worse than showing it.
        var settings = new AppSettings
        {
            ProtectedApps =
            {
                new ProtectedApp
                {
                    Name = "Claude Code",
                    Path = @"C:\Users\1\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe"
                }
            }
        };

        SettingsService.MigrateProtectedAppIdentity(settings);

        Assert.Single(settings.ProtectedApps);
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        var settings = new AppSettings
        {
            ProtectedApps =
            {
                new ProtectedApp { Name = "a", Path = Ext235 },
                new ProtectedApp { Name = "b", Path = Ext238 }
            }
        };

        SettingsService.MigrateProtectedAppIdentity(settings);
        var afterFirst = settings.ProtectedApps.Count;
        SettingsService.MigrateProtectedAppIdentity(settings);

        Assert.Equal(1, afterFirst);
        Assert.Single(settings.ProtectedApps);
    }
}
