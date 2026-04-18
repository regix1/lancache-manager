using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace LancacheManager.Core.Services;

/// <summary>
/// Looks up country/city/ISP for a public IP via ip-api.com's free tier.
///
/// Research (ipapi.is 2024 accuracy comparison study): ip-api.com reaches
/// 86.33% accuracy on country-level identification of residential IPs — well
/// within the tolerance needed for a "where is this session connecting from"
/// badge. ipinfo.io scores higher (96.57%) but requires account signup; for a
/// LAN tool with a handful of sessions per day, the free no-key endpoint is
/// the pragmatic choice.
///
/// Free endpoint rules:
///   - HTTP only (HTTPS is Pro-only).
///   - 45 requests per minute per source IP.
///   - Not allowed for commercial use.
/// Results are cached per-IP in IMemoryCache for 24 hours to stay comfortably
/// under the rate cap and to shield the UI from network hiccups.
/// </summary>
public sealed class GeoIpService
{
    private const string CachePrefix = "geoip:";
    private static readonly CompositeFormat _requestUrlFormat = CompositeFormat.Parse(
        "http://ip-api.com/json/{0}?fields=status,message,country,countryCode,regionName,city,timezone,isp,query");
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan _requestTimeout = TimeSpan.FromSeconds(4);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GeoIpService> _logger;

    public GeoIpService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<GeoIpService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<GeoIpLookup?> LookupAsync(string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return null;
        }

        if (!IPAddress.TryParse(ipAddress, out var parsed))
        {
            return null;
        }

        // Skip private / loopback / link-local — they won't resolve and will
        // just burn a rate-limit slot.
        if (IsNonPublic(parsed))
        {
            return null;
        }

        var cacheKey = CachePrefix + parsed.ToString();
        if (_cache.TryGetValue<GeoIpLookup>(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_requestTimeout);

            var client = _httpClientFactory.CreateClient();
            var url = string.Format(CultureInfo.InvariantCulture, _requestUrlFormat, Uri.EscapeDataString(parsed.ToString()));
            using var response = await client.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("GeoIP lookup for {Ip} returned HTTP {Status}", parsed, (int)response.StatusCode);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var payload = await JsonSerializer.DeserializeAsync<IpApiResponse>(stream, cancellationToken: cts.Token);

            if (payload == null || !string.Equals(payload.Status, "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("GeoIP lookup for {Ip} reported failure: {Message}", parsed, payload?.Message);
                // Cache negative result briefly to avoid hammering on obviously bad IPs.
                _cache.Set(cacheKey, (GeoIpLookup?)null, TimeSpan.FromMinutes(15));
                return null;
            }

            var result = new GeoIpLookup(
                CountryCode: payload.CountryCode,
                CountryName: payload.Country,
                RegionName: payload.RegionName,
                City: payload.City,
                Timezone: payload.Timezone,
                IspName: payload.Isp);

            _cache.Set(cacheKey, result, _cacheTtl);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("GeoIP lookup for {Ip} timed out", parsed);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GeoIP lookup for {Ip} failed", parsed);
            return null;
        }
    }

    private static bool IsNonPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 link-local
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 100.64.0.0/10 CGNAT
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            // fc00::/7 unique-local
            if (bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC) return true;
        }
        return false;
    }

    private sealed class IpApiResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("countryCode")] public string? CountryCode { get; set; }
        [JsonPropertyName("regionName")] public string? RegionName { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("timezone")] public string? Timezone { get; set; }
        [JsonPropertyName("isp")] public string? Isp { get; set; }
        [JsonPropertyName("query")] public string? Query { get; set; }
    }
}

public sealed record GeoIpLookup(
    string? CountryCode,
    string? CountryName,
    string? RegionName,
    string? City,
    string? Timezone,
    string? IspName);
