using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

/// <summary>
/// Compares network providers reported by geo APIs (T-325).
///
/// The провайдер arrives as free-form text from several services, and the same hosting company shows up
/// as "M247 Europe SRL", "M247 Ltd", "M247" or "M247EUROPE" depending on who answered. A VPN also rotates
/// its exit servers between datacentres, so the ASN alone changes without anything being wrong.
/// Matching therefore works on normalised organisation names, with the ASN as a fast "definitely same" path:
///   * equal ASN                                      → same provider
///   * one token set contains the other                → same ("m247" vs "m247 europe")
///   * first tokens share a ≥5-char prefix             → same ("cloudflare" vs "cloudflarenet")
/// Anything else is treated as a different provider — and callers must stay silent when either side is
/// unknown, because a missing geo answer is not evidence of a leak.
/// </summary>
public static class ProviderMatcher
{
    /// <summary>Legal-form noise that differs between sources for the same company.</summary>
    private static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ltd", "ltda", "llc", "inc", "incorporated", "corp", "corporation", "co", "company",
        "srl", "s.r.l", "gmbh", "ag", "sa", "s.a", "sas", "bv", "b.v", "nv", "ab", "oy", "as",
        "plc", "pte", "pty", "kft", "sp", "zoo", "ooo", "ооо", "оао", "пао", "зао",
        "limited", "holdings", "holding", "group", "networks", "network", "net", "telecom",
        "telecommunications", "communications", "communication", "hosting", "host", "isp",
        "services", "service", "solutions", "systems", "technologies", "technology", "internet"
    };

    private static readonly char[] Separators =
    {
        ' ', '\t', ',', '.', '-', '_', '/', '\\', '(', ')', '[', ']', '&', '+', '"', '\'', ':', ';'
    };

    /// <summary>
    /// True when both sides describe the same provider. Returns false when either side is unknown —
    /// callers decide what to do with "no data", and it must never be "leak".
    /// </summary>
    public static bool IsSameProvider(string? asnA, string? providerA, string? asnB, string? providerB)
    {
        if (!string.IsNullOrWhiteSpace(asnA)
            && !string.IsNullOrWhiteSpace(asnB)
            && string.Equals(asnA.Trim(), asnB.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var tokensA = Tokenize(providerA);
        var tokensB = Tokenize(providerB);

        if (tokensA.Count == 0 || tokensB.Count == 0)
        {
            return false;
        }

        if (tokensA.IsSubsetOf(tokensB) || tokensB.IsSubsetOf(tokensA))
        {
            return true;
        }

        return SharePrefix(tokensA.First(), tokensB.First());
    }

    /// <summary>True when the provider is unknown on either side — the caller must stay quiet.</summary>
    public static bool IsUnknown(string? asn, string? provider)
        => string.IsNullOrWhiteSpace(asn) && Tokenize(provider).Count == 0;

    /// <summary>Human-readable provider label for descriptions and the allow-list.</summary>
    public static string Describe(string? asn, string? provider)
    {
        var hasAsn = !string.IsNullOrWhiteSpace(asn);
        var hasProvider = !string.IsNullOrWhiteSpace(provider);

        if (hasAsn && hasProvider)
        {
            return $"{provider!.Trim()} ({asn!.Trim()})";
        }

        if (hasProvider)
        {
            return provider!.Trim();
        }

        return hasAsn ? asn!.Trim() : "неизвестен";
    }

    /// <summary>
    /// Parses one settings line into a provider identity. Accepts what <see cref="Describe"/> produces —
    /// "M247 Europe SRL (AS9009)" — as well as a bare name or a bare "AS9009".
    /// </summary>
    public static ProviderIdentity? ParseIdentity(string? line)
    {
        var value = line?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string? asn = null;
        var open = value.LastIndexOf('(');
        var close = value.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            var inner = value[(open + 1)..close].Trim();
            if (LooksLikeAsn(inner))
            {
                asn = inner.ToUpperInvariant();
                value = value[..open].Trim();
            }
        }

        if (asn is null && LooksLikeAsn(value))
        {
            return new ProviderIdentity { Asn = value.ToUpperInvariant() };
        }

        return new ProviderIdentity
        {
            Asn = asn,
            Name = string.IsNullOrWhiteSpace(value) ? null : value
        };
    }

    private static bool LooksLikeAsn(string value)
        => value.StartsWith("AS", StringComparison.OrdinalIgnoreCase)
            && value.Length > 2
            && value[2..].All(char.IsDigit);

    /// <summary>Meaningful lowercase tokens: legal forms and generic industry words dropped.</summary>
    internal static SortedSet<string> Tokenize(string? provider)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(provider))
        {
            return result;
        }

        foreach (var raw in provider.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0 || NoiseTokens.Contains(token))
            {
                continue;
            }

            // "AS13335" style tokens carry no organisation information here.
            if (token.StartsWith("as", StringComparison.Ordinal) && token.Length > 2 && token[2..].All(char.IsDigit))
            {
                continue;
            }

            result.Add(token);
        }

        return result;
    }

    private static bool SharePrefix(string a, string b)
    {
        const int MinPrefix = 5;
        if (a.Length < MinPrefix || b.Length < MinPrefix)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }
}
