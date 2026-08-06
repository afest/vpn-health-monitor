using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VpnHealthMonitor.Models;

public static class CountryNames
{
    private static readonly Dictionary<string, string> CommonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["KZ"] = "Казахстан",
        ["Kazakhstan"] = "Казахстан",
        ["Казахстан"] = "Казахстан",
        ["SE"] = "Швеция",
        ["Sweden"] = "Швеция",
        ["Швеция"] = "Швеция",
        ["FI"] = "Финляндия",
        ["Finland"] = "Финляндия",
        ["Suomi"] = "Финляндия",
        ["Финляндия"] = "Финляндия",
        ["DE"] = "Германия",
        ["Germany"] = "Германия",
        ["Deutschland"] = "Германия",
        ["Германия"] = "Германия",
        ["NL"] = "Нидерланды",
        ["Netherlands"] = "Нидерланды",
        ["Нидерланды"] = "Нидерланды",
        ["FR"] = "Франция",
        ["France"] = "Франция",
        ["Франция"] = "Франция",
        ["GB"] = "Великобритания",
        ["UK"] = "Великобритания",
        ["United Kingdom"] = "Великобритания",
        ["Великобритания"] = "Великобритания",
        ["RU"] = "Россия",
        ["Russia"] = "Россия",
        ["Россия"] = "Россия",
        ["US"] = "США",
        ["USA"] = "США",
        ["United States"] = "США",
        ["США"] = "США"
    };

    public static string ToDisplayName(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return "Неизвестно";
        }

        var trimmed = country.Trim();

        if (CommonNames.TryGetValue(trimmed, out var commonName))
        {
            return commonName;
        }

        if (trimmed.Length == 2)
        {
            try
            {
                return new RegionInfo(trimmed.ToUpperInvariant()).DisplayName;
            }
            catch (ArgumentException)
            {
                return trimmed.ToUpperInvariant();
            }
        }

        return trimmed;
    }

    public static string NormalizeCountryCode(string country)
    {
        var trimmed = country.Trim();

        if (CommonNames.ContainsKey(trimmed))
        {
            return CommonNames
                .First(item => string.Equals(item.Value, CommonNames[trimmed], StringComparison.OrdinalIgnoreCase)
                    && item.Key.Length == 2)
                .Key
                .ToUpperInvariant();
        }

        if (trimmed.Length == 2)
        {
            return trimmed.ToUpperInvariant();
        }

        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
            {
                var region = new RegionInfo(culture.Name);
                if (string.Equals(region.EnglishName, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(region.DisplayName, trimmed, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(region.NativeName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return region.TwoLetterISORegionName.ToUpperInvariant();
                }
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return trimmed.ToUpperInvariant();
    }
}
