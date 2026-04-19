using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace LancacheManager.Core.Services;

/// <summary>
/// Server-side fallback for resolving the caller's public IP address when the
/// browser cannot reach api.ipify.org (pi-hole, Lan-level DNS filters, corporate
/// proxies — all common in lancache deployments). In a typical lancache setup
/// the server and the client share a LAN, so the server's outbound public IP
/// equals the client's public IP.
///
/// Tries multiple providers in sequence (ipify → icanhazip → ipapi.co), caches
/// the result in-process so repeated calls don't hammer the provider, and
/// returns null on total failure rather than throwing.
/// </summary>
public sealed class PublicIpLookupService
{
    private const string CacheKey = "public-ip-lookup:result";
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(3);

    // Providers in preference order. Each returns a plain-text IPv4/IPv6 address
    // OR a JSON body that exposes the address under "ip". Preferring plain-text
    // keeps the parser tiny.
    private static readonly (string Url, bool IsJson)[] _providers =
    {
        ("https://api.ipify.org", false),
        ("https://icanhazip.com", false),
        ("https://ifconfig.me/ip", false),
        ("https://api.my-ip.io/ip", false),
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<PublicIpLookupService> _logger;

    public PublicIpLookupService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<PublicIpLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue<string>(CacheKey, out var cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        foreach (var (url, isJson) in _providers)
        {
            var ip = await TryProviderAsync(url, isJson, ct);
            if (!string.IsNullOrEmpty(ip))
            {
                // Global IMemoryCache has SizeLimit configured (Program.cs), so
                // every Set must declare Size — otherwise Microsoft.Extensions.Caching
                // throws "Cache entry must specify a value for Size when SizeLimit is set".
                // An IP string is tiny; 64 bytes covers IPv4 + IPv6 with headroom.
                var entryOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(_cacheTtl)
                    .SetSize(64);
                _cache.Set(CacheKey, ip, entryOptions);
                return ip;
            }
        }

        _logger.LogDebug("All public-IP providers failed or were unreachable");
        return null;
    }

    private async Task<string?> TryProviderAsync(string url, bool isJson, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_requestTimeout);

            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = (await response.Content.ReadAsStringAsync(cts.Token)).Trim();
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            string candidate = body;
            if (isJson)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<IpOnlyResponse>(body);
                    candidate = parsed?.Ip?.Trim() ?? string.Empty;
                }
                catch
                {
                    return null;
                }
            }

            return IPAddress.TryParse(candidate, out var parsedIp) ? parsedIp.ToString() : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Public-IP provider {Url} failed", url);
            return null;
        }
    }

    private sealed class IpOnlyResponse
    {
        [JsonPropertyName("ip")] public string? Ip { get; set; }
    }
}
