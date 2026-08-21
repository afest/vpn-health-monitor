using System.Text.Json;
using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>Settings written before the alias/description split must keep working untouched.</summary>
public class ExpectedInterfaceMigrationTests
{
    [Fact]
    public void Migration_FillsAliasAndDescriptionFromLegacyJson()
    {
        const string legacyJson =
            "{ \"ExpectedInterfaceName\": \"Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)\" }";
        var settings = JsonSerializer.Deserialize<AppSettings>(
            legacyJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        SettingsService.MigrateExpectedInterface(settings);

        Assert.Equal("Karing TUN Network Adapter", settings.ExpectedInterfaceAlias);
        Assert.Equal("Karing TUN Network Adapter Tunnel", settings.ExpectedInterfaceDescription);
        // Составная строка остаётся: её показывает UI, и её же читает старая версия приложения.
        Assert.Equal(
            "Karing TUN Network Adapter (Karing TUN Network Adapter Tunnel)",
            settings.ExpectedInterfaceName);
    }

    [Fact]
    public void Migration_DoesNotOverwriteExplicitFields()
    {
        var settings = new AppSettings
        {
            ExpectedInterfaceName = "Stale Display (Stale Driver)",
            ExpectedInterfaceAlias = "Karing TUN Network Adapter",
            ExpectedInterfaceDescription = "Karing TUN Network Adapter Tunnel"
        };

        SettingsService.MigrateExpectedInterface(settings);

        Assert.Equal("Karing TUN Network Adapter", settings.ExpectedInterfaceAlias);
    }

    [Fact]
    public void Migration_LeavesNoSeparateAdapterModeAlone()
    {
        var settings = new AppSettings { RouteMode = VpnRouteMode.NoSeparateAdapter };

        SettingsService.MigrateExpectedInterface(settings);

        Assert.Equal(string.Empty, settings.ExpectedInterfaceAlias);
        Assert.True(ExpectedInterface.FromSettings(settings).IsEmpty);
    }
}
