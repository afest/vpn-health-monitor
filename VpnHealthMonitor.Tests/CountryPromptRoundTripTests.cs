using VpnHealthMonitor.Models;
using VpnHealthMonitor.Services;
using Xunit;

namespace VpnHealthMonitor.Tests;

/// <summary>
/// Диалог «страна изменилась» сохраняет в настройки то же человеческое название, что показал
/// пользователю. Если сравнение потом не признает его совпадением, согласие ничего не даст —
/// плашка останется красной, и человек решит, что кнопка сломана.
/// </summary>
public class CountryPromptRoundTripTests
{
    [Theory]
    [InlineData("DE")]
    [InlineData("NL")]
    [InlineData("Germany")]
    [InlineData("Netherlands")]
    [InlineData("RU")]
    public void Saved_display_name_matches_the_snapshot_it_came_from(string snapshotCountry)
    {
        var saved = CountryNames.ToDisplayName(snapshotCountry);

        Assert.False(string.IsNullOrWhiteSpace(saved));
        Assert.True(
            HealthEvaluator.CountryMatches(saved, snapshotCountry),
            $"сохранили «{saved}», но проверка не признала это совпадением с «{snapshotCountry}»");
    }

    [Fact]
    public void Different_countries_still_mismatch_after_the_round_trip()
    {
        var saved = CountryNames.ToDisplayName("NL");
        Assert.False(HealthEvaluator.CountryMatches(saved, "DE"));
    }
}
