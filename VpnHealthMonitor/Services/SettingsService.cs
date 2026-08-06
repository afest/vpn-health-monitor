using System.IO;
using System.Text.Json;
using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

public sealed class SettingsService
{
    private static readonly string[] DefaultIpApiEndpoints =
    {
        "https://api.ipify.org?format=json",
        "https://ifconfig.me/ip",
        "https://ipinfo.io/json",
        "https://ipapi.co/json/",
        "https://ipwho.is/"
    };

    private static readonly string[] DefaultIPv6ApiEndpoints =
    {
        "https://api6.ipify.org?format=json",
        "https://ifconfig.co/ip",
        "https://icanhazip.com"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureBaseDirectories();

        if (!File.Exists(AppPaths.SettingsPath))
        {
            return Normalize(new AppSettings());
        }

        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            return Normalize(settings ?? new AppSettings());
        }
        catch
        {
            return Normalize(new AppSettings());
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureBaseDirectories();
        settings = Normalize(settings);

        await using var stream = File.Create(AppPaths.SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
    }

    public static AppSettings Normalize(AppSettings settings)
    {
        settings.IntervalSeconds = Math.Max(1, settings.IntervalSeconds);
        settings.ExpectedCountry = settings.ExpectedCountry?.Trim() ?? string.Empty;
        settings.ExpectedInterfaceName = settings.ExpectedInterfaceName?.Trim() ?? string.Empty;
        settings.ExpectedPublicIPv4 ??= new List<string>();
        settings.IpApiEndpoints ??= new List<string>();
        settings.IPv6ApiEndpoints ??= new List<string>();
        settings.ProtectedApps ??= new List<ProtectedApp>();
        settings.ConfirmedBlockedAdapters ??= new List<string>();
        settings.HttpProbeUrls ??= new List<string>();
        settings.PingHosts ??= new List<string>();
        settings.AllowedProviders ??= new List<ProviderIdentity>();
        settings.DegradedPingThresholdMs = Math.Max(1, settings.DegradedPingThresholdMs);
        settings.DegradedPacketLossThresholdPercent = Math.Clamp(settings.DegradedPacketLossThresholdPercent, 0, 100);

        // Дефолты подставляются ТОЛЬКО в пустой список (T-325). Раньше они дописывались обратно в любой
        // непустой — и адрес, который человек сознательно убрал (не доверяет сервису, он заблокирован
        // в его сети), возвращался при первом же сохранении. Пустой список — это «верни как было».
        if (settings.IpApiEndpoints.Count == 0)
        {
            settings.IpApiEndpoints.AddRange(DefaultIpApiEndpoints);
        }

        if (settings.IPv6ApiEndpoints.Count == 0)
        {
            settings.IPv6ApiEndpoints.AddRange(DefaultIPv6ApiEndpoints);
        }

        if (settings.HttpProbeUrls.Count == 0)
        {
            settings.HttpProbeUrls.AddRange(NetworkCheckService.DefaultHttpProbeUrls);
        }

        if (settings.PingHosts.Count == 0)
        {
            settings.PingHosts.AddRange(NetworkCheckService.DefaultPingHosts);
        }

        if (string.IsNullOrWhiteSpace(settings.LogsFolderPath))
        {
            settings.LogsFolderPath = AppPaths.DefaultLogsFolder;
        }

        Directory.CreateDirectory(settings.LogsFolderPath);

        return settings;
    }
}
