using System.IO;
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
        // Форма ровно как в живом settings.json: две строки, один и тот же бинарь, имена от старых версий.
        // Путь берём временный — тест не должен зависеть от того, какая версия расширения стоит на машине.
        var root = Path.Combine(Path.GetTempPath(), "vhm-tests-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, ".vscode", "extensions", "anthropic.claude-code-2.1.238-win32-x64",
            "resources", "native-binary");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "claude.exe");
        File.WriteAllText(path, string.Empty);

        try
        {
            var settings = new AppSettings
            {
                ProtectedApps =
                {
                    new ProtectedApp
                    {
                        Name = "Claude Code 2.1.235 (VS Code)",
                        Path = path,
                        RuleName = "VPN Health Monitor - Block Direct - claude.exe [479a1bb0]",
                        AddedAt = new DateTimeOffset(2026, 7, 4, 10, 28, 0, TimeSpan.FromHours(5)),
                        RulesAppliedAt = new DateTimeOffset(2026, 8, 21, 11, 3, 37, TimeSpan.FromHours(5))
                    },
                    new ProtectedApp
                    {
                        Name = "Claude Code 2.1.237 (VS Code)",
                        Path = path,
                        RuleName = "VPN Health Monitor - Block Direct - claude.exe [479a1bb0]",
                        AddedAt = new DateTimeOffset(2026, 8, 20, 8, 35, 0, TimeSpan.FromHours(5)),
                        RulesAppliedAt = new DateTimeOffset(2026, 8, 21, 11, 3, 47, TimeSpan.FromHours(5))
                    }
                }
            };

            SettingsService.MigrateProtectedAppIdentity(settings);

            var app = Assert.Single(settings.ProtectedApps);
            Assert.Equal(path, app.Path);
            Assert.StartsWith("vscode-ext:", app.IdentityKey);
            // Имя пересобрано по файлу — версия 2.1.235 больше не показывается.
            Assert.Equal("claude.exe", app.Name);
            // Самая ранняя установка защиты и самое свежее применение правил переживают склейку.
            Assert.Equal(new DateTimeOffset(2026, 7, 4, 10, 28, 0, TimeSpan.FromHours(5)), app.AddedAt);
            Assert.Equal(new DateTimeOffset(2026, 8, 21, 11, 3, 47, TimeSpan.FromHours(5)), app.RulesAppliedAt);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Merge_KeepsLastKnownName_WhenNoFileIsOnDisk()
    {
        // Оба пути мёртвые: выдумывать имя не из чего, сохраняем последнее известное.
        const string root = @"C:
o-such-root\.vscode\extensions\";
        var settings = new AppSettings
        {
            ProtectedApps =
            {
                new ProtectedApp
                {
                    Name = "Ghost 1.0.0",
                    Path = root + @"vendor.ghost-1.0.0-win32-x64in\ghost.exe"
                },
                new ProtectedApp
                {
                    Name = "Ghost 2.0.0",
                    Path = root + @"vendor.ghost-2.0.0-win32-x64in\ghost.exe"
                }
            }
        };

        SettingsService.MigrateProtectedAppIdentity(settings);

        Assert.Equal("Ghost 1.0.0", Assert.Single(settings.ProtectedApps).Name);
    }

    [Fact]
    public void Merge_PrefersTheEntryWhoseFileStillExists()
    {
        // Мёртвая строка идёт первой; выиграть должен путь, по которому файл реально есть,
        // иначе после склейки правило указывало бы в пустоту.
        var root = Path.Combine(Path.GetTempPath(), "vhm-tests-" + Guid.NewGuid().ToString("N"), ".vscode", "extensions");
        var liveFolder = Path.Combine(root, "vendor.tool-2.0.0-win32-x64", "bin");
        Directory.CreateDirectory(liveFolder);
        var livePath = Path.Combine(liveFolder, "tool.exe");
        File.WriteAllText(livePath, string.Empty);

        try
        {
            var settings = new AppSettings
            {
                ProtectedApps =
                {
                    new ProtectedApp
                    {
                        Name = "Tool 1.0.0",
                        Path = Path.Combine(root, "vendor.tool-1.0.0-win32-x64", "bin", "tool.exe"),
                        RuleName = "old-rule"
                    },
                    new ProtectedApp { Name = "Tool 2.0.0", Path = livePath, RuleName = "new-rule" }
                }
            };

            SettingsService.MigrateProtectedAppIdentity(settings);

            var app = Assert.Single(settings.ProtectedApps);
            Assert.Equal(livePath, app.Path);
            Assert.Equal("new-rule", app.RuleName);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!, recursive: true);
        }
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
