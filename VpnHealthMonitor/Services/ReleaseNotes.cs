using System.Text;
using System.Text.RegularExpressions;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Превращает markdown заметок релиза в текст для окна обновления.
///
/// Отдельно от <see cref="UpdateService"/>, потому что это чистая функция: её можно проверить
/// тестами, не выходя в сеть и не имея установленной версии.
/// </summary>
public static class ReleaseNotes
{
    public static string ToPlainText(string markdown, int maxLines = 8, int maxChars = 700)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        // Комментарии markdown — пометки для того, кто ведёт CHANGELOG, человеку они не адресованы.
        var raw = Regex.Replace(markdown, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var lines = new List<string>();
        var dropped = 0;
        var continues = false;

        foreach (var source in raw.Split('\n'))
        {
            var text = source.Trim().TrimEnd('\r');

            // Пустая строка, заголовок или разделитель разрывают абзац.
            if (text.Length == 0 || text.StartsWith('#') || IsDivider(text))
            {
                continues = false;
                continue;
            }

            var bullet = Regex.IsMatch(text, @"^[-*+]\s+");
            text = Regex.Replace(text, @"^[-*+]\s+", string.Empty);
            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");   // ссылка → её текст
            text = Regex.Replace(text, @"\*\*([^*]+)\*\*", "$1");
            text = Regex.Replace(text, "`([^`]+)`", "$1");
            text = text.Trim();

            if (text.Length == 0)
            {
                continue;
            }

            // Перенос строки в исходнике — не новый пункт: в CHANGELOG строки переносят по ширине,
            // и без склейки хвост длинного пункта встал бы отдельной строкой без «•».
            if (continues && !bullet && lines.Count > 0)
            {
                lines[^1] += " " + text;
                continue;
            }

            if (lines.Count >= maxLines)
            {
                dropped++;
                continues = false;
                continue;
            }

            lines.Add(bullet ? "• " + text : text);
            continues = true;
        }

        var result = new StringBuilder(string.Join(Environment.NewLine, lines));

        if (result.Length > maxChars)
        {
            result.Length = maxChars;
            result.Append('…');
        }

        if (dropped > 0)
        {
            result.Append(Environment.NewLine).Append($"…и ещё {dropped}");
        }

        return result.ToString().Trim();
    }

    private static bool IsDivider(string text)
        => text.All(ch => ch is '-' or '=' or '*' or '_' or ' ');
}
