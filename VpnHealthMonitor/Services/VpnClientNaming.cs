namespace VpnHealthMonitor.Services;

/// <summary>
/// Достаёт человеческое имя VPN из названия его адаптера: «hidemy.name VPN OpenVPN Adapter» → «hidemy.name».
///
/// Нужно, чтобы говорить с пользователем на его языке. Он знает, что включил HideMy — и не знает, что
/// такое AS60781 LeaseWeb: провайдер (кто держит сервер) и VPN-сервис (кому он его сдал) — разные вещи,
/// и вопрос «добавить провайдера LeaseWeb?» без имени VPN рядом просто непонятен.
/// </summary>
public static class VpnClientNaming
{
    // Технический хвост, который к имени сервиса не относится.
    private static readonly string[] NoiseWords =
    {
        "openvpn", "wireguard", "wintun", "tun", "tap-windows", "tap",
        "network", "adapter", "virtual", "ethernet", "tunnel", "vpn",
        "client", "driver", "v9", "interface"
    };

    /// <summary>
    /// Имя VPN-сервиса или null, если из названия адаптера ничего внятного не осталось
    /// (generic «TAP-Windows Adapter V9», «WireGuard Tunnel» и подобные).
    /// </summary>
    public static string? FromAdapterName(string? adapterName)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            return null;
        }

        // Берём алиас: в «Alias (Description)» описание — это модель драйвера, а не имя сервиса.
        var alias = adapterName;
        var bracket = alias.IndexOf('(');
        if (bracket > 0)
        {
            alias = alias[..bracket];
        }

        var kept = new List<string>();
        foreach (var token in alias.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cleaned = token.Trim('-', '_', ',', '.', ':');
            if (cleaned.Length == 0)
            {
                continue;
            }

            if (NoiseWords.Any(noise => string.Equals(cleaned, noise, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            kept.Add(token.Trim(',', ':'));

            // Имя сервиса стоит в начале; дальше идёт описание драйвера.
            if (kept.Count == 2)
            {
                break;
            }
        }

        var name = string.Join(' ', kept).Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// «LeaseWeb Netherlands B.V. (AS60781)» + адаптер HideMy → строка, объясняющая связь этих двух имён.
    /// Без имени VPN возвращает описание провайдера как есть.
    /// </summary>
    public static string DescribeExit(string providerLabel, string? adapterName)
    {
        var vpn = FromAdapterName(adapterName);
        return string.IsNullOrWhiteSpace(vpn)
            ? providerLabel
            : $"{providerLabel} — это сервер, через который сейчас выходит «{vpn}»";
    }
}
