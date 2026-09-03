using VpnHealthMonitor.Models;
using Xunit;

namespace VpnHealthMonitor.Tests;

public class MonitorEventTextTests
{
    [Fact]
    public void Kill_switch_event_shows_no_data_instead_of_unknown_country()
    {
        // «правила применены», «адаптеры блокировки подтверждены» — события про брандмауэр.
        // Сеть они не опрашивают, снимок пустой. «Неизвестно» здесь читалось бы как «страну
        // определить не удалось», то есть как сбой проверки, которой не было.
        var killSwitchEvent = new MonitorEvent
        {
            Description = "правила применены: 10, файл не найден: 0, ошибок: 0.",
            Country = null
        };

        Assert.Equal("н/д", killSwitchEvent.CountryText);
        Assert.Equal("н/д", killSwitchEvent.PingText);
    }

    [Fact]
    public void Real_check_still_shows_the_country()
    {
        var networkEvent = new MonitorEvent { Country = "NL", PingAverageMs = 73 };

        Assert.Equal("Нидерланды", networkEvent.CountryText);
        Assert.Equal("73 ms", networkEvent.PingText);
    }
}
