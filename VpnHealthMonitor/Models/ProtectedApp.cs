namespace VpnHealthMonitor.Models;

/// <summary>
/// A user-selected executable that VPN Health Monitor protects with a per-app
/// "closed by default" firewall rule (Block outbound on physical NICs).
/// Persisted in settings JSON.
/// </summary>
public sealed class ProtectedApp
{
    /// <summary>Display name (best-effort: file description or file name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Full path to the .exe the rule targets (matched by program path).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Identity that survives the app's own updates (MSIX family, CLI folder, VS Code extension id, else
    /// the path). This — not <see cref="Path"/> — is what makes two entries the same protected app.
    /// Empty in files written by older builds; filled in on load.
    /// </summary>
    public string IdentityKey { get; set; } = string.Empty;

    /// <summary>Predictable Windows Firewall rule name, e.g. "VPN Health Monitor - Block Direct - app [hash]".</summary>
    public string RuleName { get; set; } = string.Empty;

    /// <summary>When the app was added to the protected list.</summary>
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>When firewall rules were last successfully applied for this app (null = never).</summary>
    public DateTimeOffset? RulesAppliedAt { get; set; }
}

/// <summary>Runtime protection status of a <see cref="ProtectedApp"/> (computed, never persisted).</summary>
public enum ProtectionStatus
{
    /// <summary>Rules exist and match the current exe path — direct egress is blocked.</summary>
    Protected,

    /// <summary>No matching firewall rule is present yet.</summary>
    RulesNotApplied,

    /// <summary>The .exe no longer exists at the stored path.</summary>
    FileNotFound,

    /// <summary>
    /// A newer versioned executable was found. The rule still points at the saved path,
    /// so the newer executable is not protected until the path is updated.
    /// </summary>
    PathChanged,

    /// <summary>
    /// Правило с нашим префиксом есть в Windows, но программы нет в списке защищённых.
    /// Защита при этом действует — не видно её только приложению, и вот это как раз опасно:
    /// список в интерфейсе и реальное состояние фаервола расходятся молча.
    /// </summary>
    Untracked,

    /// <summary>
    /// Правило прошлой версии программы из списка, и из этой версии ещё запущены процессы. VS Code
    /// держит старый процесс расширения до перезапуска окна: снять правило сейчас — выпустить его
    /// мимо VPN. Взять под наблюдение такое правило нельзя: в списке одна запись на программу.
    /// </summary>
    PreviousVersionInUse,

    /// <summary>Правило прошлой версии, из которой ничего не запущено: новая уже защищена, это можно снять.</summary>
    PreviousVersionIdle,

    /// <summary>Could not determine status (query failed / inconsistent rule).</summary>
    Error
}

public static class ProtectionStatusText
{
    public static string ToDisplayText(this ProtectionStatus status) => status switch
    {
        ProtectionStatus.Protected => "Защищено",
        ProtectionStatus.RulesNotApplied => "Правила не применены",
        ProtectionStatus.FileNotFound => "Файл не найден",
        ProtectionStatus.PathChanged => "Путь устарел",
        ProtectionStatus.Untracked => "Правило вне списка",
        ProtectionStatus.PreviousVersionInUse => "Старая версия, работает",
        ProtectionStatus.PreviousVersionIdle => "Старая версия, не нужна",
        _ => "Ошибка"
    };

    /// <summary>
    /// Знак состояния для таблицы. Цвет в security-UI не может быть единственным отличием: формы
    /// подобраны так, чтобы состояния различались и на обесцвеченном скриншоте.
    /// </summary>
    public static string ToGlyph(this ProtectionStatus status) => status switch
    {
        ProtectionStatus.Protected => "✓",        // галочка
        ProtectionStatus.RulesNotApplied => "○",  // пустой круг
        ProtectionStatus.FileNotFound => "?",
        ProtectionStatus.PathChanged => "▲",      // треугольник
        ProtectionStatus.Untracked => "◆",        // ромб
        ProtectionStatus.PreviousVersionInUse => "◐", // полукруг
        ProtectionStatus.PreviousVersionIdle => "◇",  // пустой ромб
        _ => "✕"                                  // крест
    };

    public static string ToHint(this ProtectionStatus status) => status switch
    {
        ProtectionStatus.Protected => "Правило есть и указывает на текущий .exe — прямой выход закрыт.",
        ProtectionStatus.RulesNotApplied => "Правило ещё не создано. Нажми «Применить правила» (запросит UAC).",
        ProtectionStatus.FileNotFound => "Файла по сохранённому пути нет: программа удалена или переустановлена в другое место.",
        ProtectionStatus.PathChanged => "Найдена новая версия программы: правило ещё не закрывает её прямой выход. Нажми «Обновить путь».",
        ProtectionStatus.Untracked => "Правило в Windows есть и работает, но программы нет в списке защищённых — приложение не следит за её состоянием. Нажми «Взять под наблюдение».",
        ProtectionStatus.PreviousVersionInUse => "Правило прошлой версии той же программы. Она ещё может работать: из неё запущены процессы или VS Code не перезапускался целиком после обновления расширения. Правило не пускает её мимо VPN. Новая версия защищена своей строкой. Закрой все окна программы и открой снова — тогда правило можно будет снять.",
        ProtectionStatus.PreviousVersionIdle => "Правило прошлой версии той же программы, она больше не используется. Новая версия защищена своей строкой. Можно снять кнопкой «Удалить» или оставить: правило снимется само при следующем «Обновить путь».",
        _ => "Статус не определён: правило не соответствует ожидаемому или запрос к firewall не прошёл."
    };
}
