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
        settings.ExpectedInterfaceAlias = settings.ExpectedInterfaceAlias?.Trim() ?? string.Empty;
        settings.ExpectedInterfaceDescription = settings.ExpectedInterfaceDescription?.Trim() ?? string.Empty;
        settings.ExpectedInterfaceId = settings.ExpectedInterfaceId?.Trim() ?? string.Empty;
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

        MigrateExpectedInterface(settings);
        MigrateProtectedAppIdentity(settings);

        return settings;
    }

    /// <summary>
    /// Splits the legacy "Alias (Description)" blob into the fields matching actually uses. Runs on every
    /// load, so a file written before the split — or hand-edited back to a single string — keeps working.
    /// The composite value is left in place: it is what the UI shows, and an older build still reads it.
    /// </summary>
    internal static void MigrateExpectedInterface(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExpectedInterfaceAlias)
            || !string.IsNullOrWhiteSpace(settings.ExpectedInterfaceDescription))
        {
            return;
        }

        var legacy = !string.IsNullOrWhiteSpace(settings.ExpectedInterfaceName)
            ? settings.ExpectedInterfaceName
            : settings.Baseline?.InterfaceName;

        var (alias, description) = ExpectedInterface.SplitDisplay(legacy);
        if (string.IsNullOrWhiteSpace(alias) && string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        settings.ExpectedInterfaceAlias = alias;
        settings.ExpectedInterfaceDescription = description;

        if (string.IsNullOrWhiteSpace(settings.ExpectedInterfaceName))
        {
            settings.ExpectedInterfaceName = ExpectedInterface.BuildDisplay(alias, description);
        }
    }

    /// <summary>
    /// Fills in the stable identity of every protected app and collapses entries that turned out to be the
    /// same app twice — the shape a versioned update leaves behind (VS Code extension sidecar, MSIX, CLI).
    /// Merging keeps the row that still points at a file on disk, the earliest AddedAt, and the latest
    /// RulesAppliedAt, so nothing about when protection was actually applied is lost.
    /// </summary>
    internal static void MigrateProtectedAppIdentity(AppSettings settings)
    {
        var merged = new List<ProtectedApp>();
        var byKey = new Dictionary<string, ProtectedApp>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in settings.ProtectedApps)
        {
            app.Path = app.Path?.Trim() ?? string.Empty;
            app.IdentityKey = ProtectedAppIdentity.ComputeKey(app.Path);

            if (!byKey.TryGetValue(app.IdentityKey, out var existing))
            {
                byKey[app.IdentityKey] = app;
                merged.Add(app);
                continue;
            }

            Merge(existing, app);
        }

        settings.ProtectedApps = merged;
    }

    private static void Merge(ProtectedApp keep, ProtectedApp drop)
    {
        // Живой путь всегда выигрывает у мёртвого: правило, указывающее на несуществующий exe, защиты не даёт.
        if (!FileExists(keep.Path) && FileExists(drop.Path))
        {
            keep.Path = drop.Path;
            keep.RuleName = drop.RuleName;
            keep.Name = drop.Name;
        }

        // Имя пересобирается по тому файлу, который остался: иначе склейка двух строк «Claude Code 2.1.235»
        // и «2.1.237» оставляет ту версию, которой на диске уже нет.
        keep.Name = ProtectedAppNaming.RefreshIfPossible(keep.Path, keep.Name);

        if (drop.AddedAt < keep.AddedAt)
        {
            keep.AddedAt = drop.AddedAt;
        }

        if (drop.RulesAppliedAt.HasValue
            && (!keep.RulesAppliedAt.HasValue || drop.RulesAppliedAt > keep.RulesAppliedAt))
        {
            keep.RulesAppliedAt = drop.RulesAppliedAt;
        }
    }

    private static bool FileExists(string path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }
}
