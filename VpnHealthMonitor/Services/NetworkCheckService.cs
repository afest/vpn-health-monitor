using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using VpnHealthMonitor.Models;

namespace VpnHealthMonitor.Services;

public sealed class NetworkCheckService
{
    private static readonly string[] DefaultIpApiEndpoints =
    {
        "https://api.ipify.org?format=json",
        "https://ifconfig.me/ip",
        "https://ipinfo.io/json",
        "https://ipapi.co/json/",
        "https://ipwho.is/"
    };

    // ip-api.com выброшен (T-325): бесплатный тариф работает только по http, то есть внешний IP уходил
    // открытым текстом, а ответ, на котором строится вердикт «страна не совпала», можно подменить.
    private static readonly string[] GeoLookupEndpointFormats =
    {
        "https://ipinfo.io/{0}/json",
        "https://ipapi.co/{0}/json/",
        "https://ipwho.is/{0}"
    };

    private static readonly string[] DefaultIPv6ApiEndpoints =
    {
        "https://api6.ipify.org?format=json",
        "https://ifconfig.co/ip",
        "https://icanhazip.com"
    };

    // Значения по умолчанию. Сами адреса настраиваются (T-325): в сети, где заблокированы Google и
    // Cloudflare, зашитый список означал бы вечное «НЕТ ИНТЕРНЕТА» у тех, кому приложение нужнее всего.
    public static readonly string[] DefaultHttpProbeUrls =
    {
        "https://www.cloudflare.com/cdn-cgi/trace",
        "https://www.google.com/generate_204",
        "https://www.msftconnecttest.com/connecttest.txt"
    };

    public static readonly string[] DefaultPingHosts = { "1.1.1.1", "8.8.8.8" };

    // Кросс-сверка нескольких IP-источников ("IP APIs disagreed") — дорогая (веер), делаем редко.
    private static readonly TimeSpan CrossCheckInterval = TimeSpan.FromMinutes(10);

    // Backoff для endpoint'а, ответившего 429/503 без внятного Retry-After.
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinRateLimitCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRateLimitCooldown = TimeSpan.FromMinutes(30);

    // SocketsHttpHandler с ограниченным временем жизни соединений: пул не залипает за старый
    // маршрут после переключения VPN — ровно тот момент, который монитор обязан ловить точно.
    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromSeconds(90),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.All
    });

    // Состояние щадящего опроса. Доступ сериализован вызывающим кодом (RunAsync под _checkLock).
    private readonly Dictionary<string, DateTimeOffset> _hostCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private IpLookupResult? _cachedIpResult;
    private DateTimeOffset _lastCrossCheckAt;
    private int _ipRotationCursor;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    public async Task<NetworkSnapshot> RunAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var checkedAt = DateTimeOffset.Now;
        var errors = new List<string>();

        // Внешний IP-адрес опрашивается КАЖДЫЙ цикл (1 запрос ротацией, не веером) — это основной
        // и для proxy-VPN единственный быстрый сигнал смены сети. Щадим не IP, а лимитированные
        // geo-API: страна/ASN берётся из кэша, пока IP не сменился (geo меняется только вместе с IP).
        var ipTask = ResolveExternalIpAsync(settings, checkedAt, errors, cancellationToken);
        var ipv6Task = settings.EnableIPv6LeakCheck
            ? ResolveExternalIPv6Async(settings, checkedAt, errors, cancellationToken)
            : Task.FromResult(new IPv6LookupResult(null, "Выключено"));
        var dnsTask = settings.EnableDnsCheck
            ? Task.FromResult(DetectDnsServers(errors))
            : Task.FromResult(new List<string>());
        var httpTask = CheckHttpAvailabilityAsync(settings, errors, cancellationToken);
        var pingTask = CheckPingAsync(settings, errors, cancellationToken);

        await Task.WhenAll(ipTask, ipv6Task, dnsTask, httpTask, pingTask);

        var ipResult = ipTask.Result;
        var ipv6Result = ipv6Task.Result;
        var pingResult = pingTask.Result;

        return new NetworkSnapshot
        {
            CheckedAt = checkedAt,
            ExternalIPv4 = ipResult.Ip,
            ExternalIPv6 = ipv6Result.Ip,
            IPv6CheckStatus = ipv6Result.Status,
            Country = ipResult.Country,
            Asn = ipResult.Asn,
            Provider = ipResult.Provider,
            InterfaceName = DetectDefaultInterface(),
            IpApiResults = ipResult.IpApiResults,
            DnsServers = dnsTask.Result,
            HttpAvailable = httpTask.Result,
            PingAverageMs = pingResult.AverageMs,
            PacketLossPercent = pingResult.PacketLossPercent,
            PingAvailable = pingResult.Successes > 0,
            PingAttempts = pingResult.Attempts,
            PingSuccesses = pingResult.Successes,
            Errors = errors
        };
    }

    private async Task<IpLookupResult> ResolveExternalIpAsync(
        AppSettings settings,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var crossCheck = _lastCrossCheckAt == default || (now - _lastCrossCheckAt) >= CrossCheckInterval;
        var result = await FetchExternalIpAsync(settings, now, crossCheck, errors, cancellationToken);

        if (!string.IsNullOrWhiteSpace(result.Ip))
        {
            _cachedIpResult = result;
            if (crossCheck)
            {
                _lastCrossCheckAt = now;
            }
        }

        return result;
    }

    private async Task<IpLookupResult> FetchExternalIpAsync(
        AppSettings settings,
        DateTimeOffset now,
        bool crossCheck,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var endpoints = settings.IpApiEndpoints.Count == 0
            ? DefaultIpApiEndpoints
            : settings.IpApiEndpoints.ToArray();

        if (crossCheck)
        {
            return await CrossCheckExternalIpAsync(endpoints, now, errors, cancellationToken);
        }

        var count = endpoints.Length;

        // Живые endpoint'ы по порядку ротации (начиная с курсора, исключая те, что в cooldown).
        var ordered = new List<(int Index, string Endpoint)>(count);
        for (var offset = 0; offset < count; offset++)
        {
            var index = (_ipRotationCursor + offset) % count;
            if (!IsHostOnCooldown(endpoints[index], now))
            {
                ordered.Add((index, endpoints[index]));
            }
        }

        if (ordered.Count == 0)
        {
            return new IpLookupResult();
        }

        // 1) Основной источник (курсор). В норме это единственный сетевой запрос за цикл (1 вместо веера).
        var primary = ordered[0];
        var primaryResult = await FetchIpEndpointAsync(primary.Endpoint, AddressFamily.InterNetwork, cancellationToken);
        HandleNonIpOutcome(primaryResult, primary.Endpoint, now, errors);

        if (!string.IsNullOrWhiteSpace(primaryResult.Ip))
        {
            _ipRotationCursor = primary.Index;
            return await BuildResolvedAsync(primaryResult, new[] { primaryResult }, now, errors, cancellationToken);
        }

        // 2) Основной не дал IP — параллельный веер по остальным живым. Быстрый fallback вместо
        //    последовательного перебора с таймаутами: критично в момент пропажи VPN, когда мёртвы все
        //    (последовательно это N×timeout ≈ 20с, веером ≈ один timeout).
        var rest = ordered.Skip(1).ToArray();
        if (rest.Length == 0)
        {
            return new IpLookupResult { IpApiResults = FormatIpApiResults(new[] { primaryResult }) };
        }

        var restResults = (await Task.WhenAll(rest.Select(item =>
            FetchIpEndpointAsync(item.Endpoint, AddressFamily.InterNetwork, cancellationToken)))).ToList();

        for (var i = 0; i < rest.Length; i++)
        {
            HandleNonIpOutcome(restResults[i], rest[i].Endpoint, now, errors);
        }

        var attempted = new List<IpLookupResult> { primaryResult };
        attempted.AddRange(restResults);

        for (var i = 0; i < restResults.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(restResults[i].Ip))
            {
                _ipRotationCursor = rest[i].Index;
                return await BuildResolvedAsync(restResults[i], attempted, now, errors, cancellationToken);
            }
        }

        return new IpLookupResult { IpApiResults = FormatIpApiResults(attempted) };
    }

    private void HandleNonIpOutcome(IpLookupResult result, string endpoint, DateTimeOffset now, List<string> errors)
    {
        if (result.RateLimited)
        {
            // 429/503 — временно недоступен, не ошибка проверки: в errors не пишем, endpoint в cooldown.
            ApplyHostCooldown(endpoint, now, result.RetryAfter);
        }
        else if (!string.IsNullOrWhiteSpace(result.Error))
        {
            AddError(errors, $"{result.Endpoint}: {result.Error}");
        }
    }

    private async Task<IpLookupResult> BuildResolvedAsync(
        IpLookupResult selected,
        IReadOnlyCollection<IpLookupResult> attempted,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var resolved = await EnsureGeoAsync(selected, now, errors, cancellationToken);
        return new IpLookupResult
        {
            Endpoint = resolved.Endpoint,
            Ip = resolved.Ip,
            Country = resolved.Country,
            Asn = resolved.Asn,
            Provider = resolved.Provider,
            IpApiResults = FormatIpApiResults(attempted)
        };
    }

    private async Task<IpLookupResult> CrossCheckExternalIpAsync(
        string[] endpoints,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var live = endpoints.Where(endpoint => !IsHostOnCooldown(endpoint, now)).ToArray();
        if (live.Length == 0)
        {
            return new IpLookupResult();
        }

        var results = (await Task.WhenAll(live.Select(endpoint =>
            FetchIpEndpointAsync(endpoint, AddressFamily.InterNetwork, cancellationToken)))).ToList();

        foreach (var result in results)
        {
            if (result.RateLimited)
            {
                ApplyHostCooldown(result.Endpoint, now, result.RetryAfter);
            }
            else if (!string.IsNullOrWhiteSpace(result.Error))
            {
                AddError(errors, $"{result.Endpoint}: {result.Error}");
            }
        }

        var successfulResults = results.Where(result => !string.IsNullOrWhiteSpace(result.Ip)).ToList();
        if (successfulResults.Count == 0)
        {
            return new IpLookupResult { IpApiResults = FormatIpApiResults(results) };
        }

        var selected = successfulResults.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Country))
            ?? successfulResults[0];

        var distinctIps = successfulResults
            .Select(result => result.Ip)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctIps.Count > 1)
        {
            AddError(errors, $"IP APIs disagreed: {string.Join(", ", distinctIps)}");
        }

        var resolved = await EnsureGeoAsync(selected, now, errors, cancellationToken);
        return new IpLookupResult
        {
            Endpoint = resolved.Endpoint,
            Ip = resolved.Ip,
            Country = resolved.Country,
            Asn = resolved.Asn,
            Provider = resolved.Provider,
            IpApiResults = FormatIpApiResults(results)
        };
    }

    private async Task<IpLookupResult> EnsureGeoAsync(
        IpLookupResult primary,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(primary.Ip) || !string.IsNullOrWhiteSpace(primary.Country))
        {
            return primary;
        }

        // Кэш geo: страна/ASN меняются только вместе с IP. Если IP тот же, что в прошлом цикле —
        // не дёргаем geo-lookup (это и есть экономия лимитированных API: ipapi.co/ipinfo.io не
        // опрашиваются каждый цикл, только при реальной смене IP).
        if (_cachedIpResult is not null
            && string.Equals(_cachedIpResult.Ip, primary.Ip, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_cachedIpResult.Country))
        {
            return MergeIpResults(primary, _cachedIpResult);
        }

        var geo = await FetchGeoForIpAsync(primary.Ip!, now, errors, cancellationToken);
        return geo is not null ? MergeIpResults(primary, geo) : primary;
    }

    private async Task<IPv6LookupResult> ResolveExternalIPv6Async(
        AppSettings settings,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var endpoints = settings.IPv6ApiEndpoints.Count == 0
            ? DefaultIPv6ApiEndpoints
            : settings.IPv6ApiEndpoints.ToArray();

        // Ротация вместо веера: первый успешный источник. IPv6-leak — тоже сигнал, опрашиваем каждый цикл.
        foreach (var endpoint in endpoints)
        {
            if (IsHostOnCooldown(endpoint, now))
            {
                continue;
            }

            var result = await FetchIpEndpointAsync(endpoint, AddressFamily.InterNetworkV6, cancellationToken);

            if (result.RateLimited)
            {
                ApplyHostCooldown(endpoint, now, result.RetryAfter);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                AddError(errors, $"IPv6 {result.Endpoint}: {result.Error}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Ip))
            {
                return new IPv6LookupResult(result.Ip, "Найден");
            }
        }

        return new IPv6LookupResult(null, "Не обнаружен / проверка не прошла");
    }

    private async Task<IpLookupResult?> FetchGeoForIpAsync(
        string ip,
        DateTimeOffset now,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var escapedIp = Uri.EscapeDataString(ip);

        // Ротация по geo-источникам: первый, отдавший страну. Не веером — geo-lookup дёргается
        // редко (только при реальной смене IP), один валидный ответ достаточен.
        foreach (var format in GeoLookupEndpointFormats)
        {
            var endpoint = string.Format(format, escapedIp);

            if (IsHostOnCooldown(endpoint, now))
            {
                continue;
            }

            var result = await FetchIpEndpointAsync(endpoint, AddressFamily.InterNetwork, cancellationToken);

            if (result.RateLimited)
            {
                ApplyHostCooldown(endpoint, now, result.RetryAfter);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                AddError(errors, $"{result.Endpoint}: {result.Error}");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(result.Country))
            {
                return result;
            }
        }

        return null;
    }

    private static IpLookupResult MergeIpResults(IpLookupResult primary, IpLookupResult geo)
    {
        return new IpLookupResult
        {
            Endpoint = primary.Endpoint,
            Ip = primary.Ip,
            Country = geo.Country ?? primary.Country,
            Asn = geo.Asn ?? primary.Asn,
            Provider = geo.Provider ?? primary.Provider
        };
    }

    private async Task<IpLookupResult> FetchIpEndpointAsync(
        string endpoint,
        AddressFamily addressFamily,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new IpLookupResult { Endpoint = endpoint, Error = "invalid URI" };
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("VpnHealthMonitor/1.0");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                return new IpLookupResult
                {
                    Endpoint = endpoint,
                    RateLimited = true,
                    RetryAfter = ReadRetryAfter(response)
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new IpLookupResult { Endpoint = endpoint, Error = $"HTTP {(int)response.StatusCode}" };
            }

            var content = (await response.Content.ReadAsStringAsync(timeoutCts.Token)).Trim();

            if (content.StartsWith("{", StringComparison.Ordinal))
            {
                return ParseIpJson(endpoint, content, addressFamily);
            }

            var addressLabel = addressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
            return IPAddress.TryParse(content, out var address) && address.AddressFamily == addressFamily
                ? new IpLookupResult { Endpoint = endpoint, Ip = content }
                : new IpLookupResult { Endpoint = endpoint, Error = $"response did not contain an {addressLabel} address" };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new IpLookupResult { Endpoint = endpoint, Error = "timeout" };
        }
        catch (Exception ex)
        {
            return new IpLookupResult { Endpoint = endpoint, Error = ex.Message };
        }
    }

    private static IpLookupResult ParseIpJson(string endpoint, string json, AddressFamily addressFamily)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var ip = TryGetString(root, "ip") ?? TryGetString(root, "query");
            if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != addressFamily)
            {
                var addressLabel = addressFamily == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4";
                return new IpLookupResult { Endpoint = endpoint, Error = $"JSON did not contain an {addressLabel} address" };
            }

            var country = TryGetString(root, "country")
                ?? TryGetString(root, "country_code")
                ?? TryGetString(root, "countryCode");
            var org = TryGetString(root, "org")
                ?? TryGetString(root, "organization")
                ?? TryGetString(root, "isp");
            var asn = NormalizeAsn(TryGetString(root, "asn"));
            var asLine = TryGetString(root, "as");

            if (root.TryGetProperty("connection", out var connection) && connection.ValueKind == JsonValueKind.Object)
            {
                asn ??= NormalizeAsn(TryGetString(connection, "asn"));
                org ??= TryGetString(connection, "org")
                    ?? TryGetString(connection, "organization")
                    ?? TryGetString(connection, "isp");
            }

            if (string.IsNullOrWhiteSpace(asn) && !string.IsNullOrWhiteSpace(asLine))
            {
                var (asLineAsn, asLineProvider) = SplitOrg(asLine);
                asn = asLineAsn;
                org ??= asLineProvider;
            }

            var (orgAsn, provider) = SplitOrg(org);
            asn ??= orgAsn;

            return new IpLookupResult
            {
                Endpoint = endpoint,
                Ip = ip,
                Country = country,
                Asn = asn,
                Provider = provider
            };
        }
        catch (JsonException ex)
        {
            return new IpLookupResult { Endpoint = endpoint, Error = ex.Message };
        }
    }

    private async Task<bool> CheckHttpAvailabilityAsync(
        AppSettings settings,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var configured = settings.HttpProbeUrls.Count > 0 ? settings.HttpProbeUrls : DefaultHttpProbeUrls.ToList();
        var uris = configured
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null)
            .Select(uri => uri!)
            .ToArray();

        if (uris.Length == 0)
        {
            AddError(errors, "HTTP-пробы: в настройках нет ни одного корректного адреса.");
            return false;
        }

        var tasks = uris.Select(uri => CheckHttpEndpointAsync(uri, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var result in results.Where(result => !result.Success))
        {
            AddError(errors, $"{result.Endpoint}: {result.Error}");
        }

        return results.Any(result => result.Success);
    }

    private async Task<HttpProbeResult> CheckHttpEndpointAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("VpnHealthMonitor/1.0");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            return new HttpProbeResult(uri.ToString(), (int)response.StatusCode < 500, response.StatusCode.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HttpProbeResult(uri.ToString(), false, "timeout");
        }
        catch (Exception ex)
        {
            return new HttpProbeResult(uri.ToString(), false, ex.Message);
        }
    }

    private static async Task<PingProbeResult> CheckPingAsync(
        AppSettings settings,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        var attempts = 0;
        var successes = 0;
        var latencies = new List<long>();
        var hosts = settings.PingHosts.Count > 0 ? settings.PingHosts : DefaultPingHosts.ToList();

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                using var ping = new Ping();
                var stopwatch = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(host, 3000);
                stopwatch.Stop();

                if (reply.Status == IPStatus.Success)
                {
                    successes++;
                    latencies.Add(reply.RoundtripTime > 0 ? reply.RoundtripTime : stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    AddError(errors, $"ping {host}: {reply.Status}");
                }
            }
            catch (Exception ex) when (ex is PingException or InvalidOperationException)
            {
                AddError(errors, $"ping {host}: {ex.Message}");
            }
        }

        var loss = attempts == 0 ? 100 : (attempts - successes) * 100.0 / attempts;

        return new PingProbeResult(
            attempts,
            successes,
            latencies.Count > 0 ? latencies.Average() : null,
            loss);
    }

    private static List<string> DetectDnsServers(List<string> errors)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up)
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(item => item.GetIPProperties().DnsAddresses)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                .Select(address => address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or InvalidOperationException)
        {
            AddError(errors, $"DNS servers: {ex.Message}");
            return new List<string>();
        }
    }

    private static string DetectDefaultInterface()
    {
        try
        {
            var networkInterface = FindInterfaceByBestRoute()
                ?? NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up)
                .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .FirstOrDefault(item =>
                {
                    var properties = item.GetIPProperties();
                    return properties.GatewayAddresses.Any(gateway =>
                        gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                });

            return networkInterface is null
                ? "Unknown"
                : $"{networkInterface.Name} ({networkInterface.Description})";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static NetworkInterface? FindInterfaceByBestRoute()
    {
        var destinationBytes = IPAddress.Parse("8.8.8.8").GetAddressBytes();
        var destination = BitConverter.ToUInt32(destinationBytes, 0);
        if (GetBestInterface(destination, out var bestIfIndex) != 0)
        {
            return null;
        }

        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var ipv4 = item.GetIPProperties().GetIPv4Properties();
                if (ipv4.Index == bestIfIndex)
                {
                    return item;
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return null;
    }

    private bool IsHostOnCooldown(string endpoint, DateTimeOffset now)
    {
        var host = TryGetHost(endpoint);
        return host is not null
            && _hostCooldownUntil.TryGetValue(host, out var until)
            && until > now;
    }

    private void ApplyHostCooldown(string endpoint, DateTimeOffset now, TimeSpan? retryAfter)
    {
        var host = TryGetHost(endpoint);
        if (host is null)
        {
            return;
        }

        var cooldown = retryAfter ?? DefaultRateLimitCooldown;
        if (cooldown < MinRateLimitCooldown)
        {
            cooldown = MinRateLimitCooldown;
        }
        else if (cooldown > MaxRateLimitCooldown)
        {
            cooldown = MaxRateLimitCooldown;
        }

        _hostCooldownUntil[host] = now + cooldown;
    }

    private static string? TryGetHost(string endpoint)
        => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta.HasValue)
        {
            return retryAfter.Delta;
        }

        if (retryAfter.Date.HasValue)
        {
            var delta = retryAfter.Date.Value - DateTimeOffset.Now;
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static void AddError(List<string> errors, string error)
    {
        lock (errors)
        {
            errors.Add(error);
        }
    }

    private static (string? Asn, string? Provider) SplitOrg(string? org)
    {
        if (string.IsNullOrWhiteSpace(org))
        {
            return (null, null);
        }

        var parts = org.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var asn = NormalizeAsn(parts[0]);
        if (asn is null)
        {
            return (null, org);
        }

        if (parts.Length == 1)
        {
            return (asn, null);
        }

        return (asn, parts[1]);
    }

    private static string? NormalizeAsn(string? asn)
    {
        if (string.IsNullOrWhiteSpace(asn))
        {
            return null;
        }

        var trimmed = asn.Trim();
        if (trimmed.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.ToUpperInvariant();
        }

        return trimmed.All(char.IsDigit) ? $"AS{trimmed}" : null;
    }

    private static List<string> FormatIpApiResults(IEnumerable<IpLookupResult> results)
    {
        return results
            .Select(result =>
            {
                var host = Uri.TryCreate(result.Endpoint, UriKind.Absolute, out var uri)
                    ? uri.Host
                    : result.Endpoint;

                if (!string.IsNullOrWhiteSpace(result.Ip))
                {
                    var country = CountryNames.ToDisplayName(result.Country);
                    return country == "Неизвестно" ? $"{host}: {result.Ip}" : $"{host}: {result.Ip} ({country})";
                }

                if (result.RateLimited)
                {
                    return $"{host}: лимит запросов (429)";
                }

                return $"{host}: ошибка";
            })
            .ToList();
    }

    private sealed record HttpProbeResult(string Endpoint, bool Success, string? Error);

    private sealed record IPv6LookupResult(string? Ip, string Status);

    private sealed record PingProbeResult(int Attempts, int Successes, double? AverageMs, double PacketLossPercent);

    private sealed class IpLookupResult
    {
        public string Endpoint { get; init; } = string.Empty;

        public string? Ip { get; init; }

        public string? Country { get; init; }

        public string? Asn { get; init; }

        public string? Provider { get; init; }

        public string? Error { get; init; }

        public bool RateLimited { get; init; }

        public TimeSpan? RetryAfter { get; init; }

        public List<string> IpApiResults { get; init; } = new();
    }
}
