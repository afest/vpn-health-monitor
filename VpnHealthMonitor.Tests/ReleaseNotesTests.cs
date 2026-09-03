using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public void Bullets_become_dots_and_headings_go_away()
    {
        var text = ReleaseNotes.ToPlainText("## Что нового\n\n- Первый пункт\n- Второй пункт\n");

        Assert.Equal("• Первый пункт" + Environment.NewLine + "• Второй пункт", text);
    }

    [Fact]
    public void Wrapped_line_joins_its_bullet_instead_of_becoming_a_new_one()
    {
        // В CHANGELOG строки переносят по ширине. Без склейки хвост встал бы отдельной строкой
        // без «•» и читался как отдельное изменение.
        var text = ReleaseNotes.ToPlainText("- Длинный пункт, который\n  продолжается на следующей строке\n- Второй");

        Assert.Equal(
            "• Длинный пункт, который продолжается на следующей строке" + Environment.NewLine + "• Второй",
            text);
    }

    [Fact]
    public void Markdown_comments_never_reach_the_reader()
    {
        // Служебная пометка для того, кто ведёт CHANGELOG, человеку в окне не адресована.
        var text = ReleaseNotes.ToPlainText("<!-- не забыть про версию -->\n- Настоящий пункт");

        Assert.DoesNotContain("не забыть", text);
        Assert.Equal("• Настоящий пункт", text);
    }

    [Fact]
    public void Links_and_emphasis_collapse_to_their_text()
    {
        var text = ReleaseNotes.ToPlainText("- Смотри [релиз](https://example.com), это **важно** и `быстро`");

        Assert.Equal("• Смотри релиз, это важно и быстро", text);
    }

    [Fact]
    public void Extra_lines_are_counted_not_silently_dropped()
    {
        var markdown = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"- Пункт {i}"));
        var text = ReleaseNotes.ToPlainText(markdown, maxLines: 3);

        Assert.Contains("• Пункт 1", text);
        Assert.Contains("…и ещё 9", text);
        Assert.DoesNotContain("• Пункт 4", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("## Только заголовок")]
    [InlineData("---")]
    public void Nothing_to_show_yields_empty_string(string markdown)
        => Assert.Equal(string.Empty, ReleaseNotes.ToPlainText(markdown));
}
