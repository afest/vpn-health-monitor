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
    /// The stored path is gone, but the same Store/MSIX package was found at a new (versioned) path.
    /// The rule still points at the dead path, so protection is NOT in effect until the path is updated.
    /// </summary>
    PathChanged,

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
        _ => "✕"                                  // крест
    };

    public static string ToHint(this ProtectionStatus status) => status switch
    {
        ProtectionStatus.Protected => "Правило есть и указывает на текущий .exe — прямой выход закрыт.",
        ProtectionStatus.RulesNotApplied => "Правило ещё не создано. Нажми «Применить правила» (запросит UAC).",
        ProtectionStatus.FileNotFound => "Файла по сохранённому пути нет: программа удалена или переустановлена в другое место.",
        ProtectionStatus.PathChanged => "Правило указывает на мёртвый путь — защита НЕ действует. Нажми «Обновить путь».",
        _ => "Статус не определён: правило не соответствует ожидаемому или запрос к firewall не прошёл."
    };
}
