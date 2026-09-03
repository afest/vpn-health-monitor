using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Проверка и установка обновлений через Velopack: релизы GitHub-репозитория проекта.
///
/// Работает только в установленной версии. Запуск из исходников и exe, скопированный руками, ничего
/// не проверяют и в сеть не ходят: Velopack не знает, где лежит приложение, и обновлять ему нечего.
/// В этом случае <see cref="IsAvailable"/> отвечает «нет», а UI показывает это словами, а не молчит —
/// иначе кнопка «проверить обновления» выглядит сломанной.
///
/// Про молчание. Фоновая проверка при старте необязательна: нет интернета, GitHub отдал 403, фид не
/// отвечает — всё это уходит в лог и человеку не показывается. Диалог с ошибкой при каждом запуске
/// раздражает сильнее, чем помогает. Ручную проверку, наоборот, доводим до ответа всегда: человек
/// нажал кнопку и ждёт результата.
/// </summary>
public sealed class UpdateService
{
    // Репозиторий, из релизов которого берутся обновления.
    public const string RepositoryUrl = "https://github.com/afest/vpn-health-monitor";

    // Переопределение источника для проверки цикла обновления без сети: путь к папке или URL.
    private const string FeedOverrideVariable = "VPNHEALTHMONITOR_UPDATE_FEED";

    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private UpdateInfo? _pending;

    /// <summary>Обновление, найденное последней проверкой, или null.</summary>
    public UpdateInfo? Pending => _pending;

    public static string Feed
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable(FeedOverrideVariable);
            return string.IsNullOrWhiteSpace(custom) ? RepositoryUrl : custom.Trim();
        }
    }

    /// <summary>Версия приложения для показа в интерфейсе.</summary>
    public static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                // "1.1.7+<commit>" — хеш коммита человеку в окне не нужен.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        }
    }

    /// <summary>Есть ли смысл говорить об обновлениях: только установленная версия умеет обновляться.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return BuildManager().IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    private static UpdateManager BuildManager()
    {
        var feed = Feed;
        if (feed.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            || feed.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateManager(new GithubSource(feed, accessToken: null, prerelease: false));
        }

        return new UpdateManager(feed);
    }

    /// <summary>
    /// Ищет обновление. При quiet=true ошибки гасятся и возвращается null; при quiet=false —
    /// пробрасываются, чтобы на явное нажатие кнопки человек получил внятный ответ.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(bool quiet, Action<string>? log = null)
    {
        if (!IsAvailable)
        {
            if (!quiet)
            {
                throw new InvalidOperationException(
                    "Обновления работают только в установленной версии. Эта копия запущена не через установщик.");
            }

            return null;
        }

        // Фоновая проверка при старте и кнопка в настройках могут совпасть — вторую пропускаем.
        if (!await _checkLock.WaitAsync(0))
        {
            log?.Invoke("проверка обновлений уже идёт — пропускаю");
            return null;
        }

        try
        {
            var info = await Task.Run(() => BuildManager().CheckForUpdates());
            _pending = info;
            log?.Invoke(info is null
                ? "обновлений нет"
                : $"доступно обновление: {VersionOf(info)}");
            return info;
        }
        catch (Exception ex) when (quiet)
        {
            log?.Invoke($"проверка обновлений не удалась ({ex.GetType().Name}: {ex.Message}) — молчу");
            return null;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>
    /// Скачивает и применяет обновление, затем перезапускает приложение. Возврата из этого метода
    /// при успехе нет — процесс завершается Velopack'ом.
    /// </summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, Action<int>? onProgress = null)
    {
        var manager = BuildManager();
        await Task.Run(() => manager.DownloadUpdates(info, p => onProgress?.Invoke(p)));
        manager.ApplyUpdatesAndRestart(info);
    }

    public static string VersionOf(UpdateInfo info)
    {
        try
        {
            return info.TargetFullRelease.Version.ToString();
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>Страница релиза на GitHub: собирается напрямую, а не из фида — фид может быть локальным.</summary>
    public static string ReleasePageUrl(UpdateInfo info)
        => $"{RepositoryUrl}/releases/tag/v{VersionOf(info)}";

    /// <summary>
    /// Заметки релиза обычным текстом; пустая строка, если их нет. Markdown упрощается, потому что
    /// показывается в обычном окне: заголовки выкидываются (номер версии уже в шапке), пункты
    /// отбиваются «•», ссылки остаются текстом.
    /// </summary>
    public static string NotesOf(UpdateInfo info, int maxLines = 8, int maxChars = 700)
    {
        string raw;
        try
        {
            raw = info.TargetFullRelease.NotesMarkdown ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return ReleaseNotes.ToPlainText(raw, maxLines, maxChars);
    }
}
