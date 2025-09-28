using System.Text.RegularExpressions;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly ILogger<LogParserService> _logger;

    // Primary regex to match standard lancache log format:
    // [service] IP / - - - [timestamp] "METHOD URL HTTP/version" status bytes "-" "user-agent" "HIT/MISS" "domain" "-"
    private static readonly Regex LogRegex = new(
        @"^\[(?<service>[\w\.]+)\]\s+(?<ip>[\d\.]+).*?\[(?<time>[^\]]+)\].*?""(?:GET|POST|HEAD|PUT|OPTIONS|DELETE|PATCH)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+)\s+""[^""]*""\s+""[^""]*""\s+""(?<cache>HIT|MISS|EXPIRED|UPDATING|STALE|BYPASS|REVALIDATED|-)?""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Fallback regex with more lenient matching - captures logs even without cache status field
    private static readonly Regex FallbackLogRegex = new(
        @"^\[(?<service>[\w\.]+)\]\s+(?<ip>[\d\.]+).*?\[(?<time>[^\]]+)\].*?""(?:GET|POST|HEAD|PUT|OPTIONS|DELETE|PATCH)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex pattern for Steam depot extraction
    private static readonly Regex DepotRegex = new(@"/depot/(\d+)/", RegexOptions.Compiled);

    public LogParserService(ILogger<LogParserService> logger)
    {
        _logger = logger;
    }

    public LogEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            // Don't skip anything - process ALL entries
            // if (line.Contains("lancache-heartbeat"))
            // {
            //     return null;
            // }

            // Try primary regex first
            var match = LogRegex.Match(line);
            if (!match.Success)
            {
                // Try fallback regex if primary fails
                match = FallbackLogRegex.Match(line);
                if (!match.Success)
                {
                    // Log first few failures for debugging
                    if (_logger.IsEnabled(LogLevel.Trace))
                    {
                        _logger.LogTrace($"Failed to parse line with both regexes: {line.Substring(0, Math.Min(100, line.Length))}...");
                    }
                    return null;
                }
                else
                {
                    _logger.LogTrace("Used fallback regex for parsing");
                }
            }

            // Parse matched groups
            var serviceRaw = match.Groups["service"].Value.ToLower();
            // If service is an IP address, default to "unknown"
            var service = System.Net.IPAddress.TryParse(serviceRaw, out _) ? "unknown" : serviceRaw;

            var clientIp = match.Groups["ip"].Value;
            var url = match.Groups["url"].Value;
            var statusCode = int.Parse(match.Groups["status"].Value);
            var bytesServed = long.Parse(match.Groups["bytes"].Value);

            // Cache status might not exist in fallback regex
            var cacheStatusRaw = match.Groups["cache"]?.Value?.ToUpper() ?? "";
            // Handle "-" or empty cache status
            var cacheStatus = (string.IsNullOrEmpty(cacheStatusRaw) || cacheStatusRaw == "-") ? "UNKNOWN" : cacheStatusRaw;

            var timestamp = ParseTimestamp(match.Groups["time"].Value);

            // Don't skip localhost - process ALL entries
            // if (clientIp == "127.0.0.1" && bytesServed < 1000)
            // {
            //     return null;
            // }

            // Extract depot ID from Steam URLs only
            var depotId = ExtractDepotIdFromUrl(url, service);

            _logger.LogTrace($"Parsed: {service} {clientIp} {bytesServed} bytes {cacheStatus}" +
                (depotId.HasValue ? $" depot:{depotId.Value}" : ""));

            return new LogEntry
            {
                Service = service,
                ClientIp = clientIp,
                Url = url,
                StatusCode = statusCode,
                BytesServed = bytesServed,
                CacheStatus = cacheStatus,
                Timestamp = timestamp,
                DepotId = depotId
            };
        }
        catch (Exception ex)
        {
            _logger.LogTrace($"Error parsing line: {ex.Message}");
        }

        return null;
    }

    // Replace with more flexible parsing:
    private DateTime ParseTimestamp(string timestamp)
    {
        try
        {
            // Remove any timezone info
            timestamp = System.Text.RegularExpressions.Regex.Replace(timestamp, @"\s[+-]\d{4}$", "");
            
            // Try multiple formats
            string[] formats = new[]
            {
                "dd/MMM/yyyy:HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss",
                "dd/MMM/yyyy HH:mm:ss",
                "yyyy-MM-ddTHH:mm:ss"
            };
            
            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(timestamp, 
                    format, 
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var result))
                {
                    return result.ToUniversalTime();
                }
            }
            
            // Fallback to general parse
            if (DateTime.TryParse(timestamp, out var fallbackResult))
            {
                return fallbackResult.ToUniversalTime();
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace($"Error parsing timestamp '{timestamp}': {ex.Message}");
        }
        
        _logger.LogWarning($"Failed to parse timestamp: {timestamp}");
        return DateTime.UtcNow;
    }

    /// <summary>
    /// Extract depot ID from Steam URLs only, ignoring WSUS URLs
    /// </summary>
    private uint? ExtractDepotIdFromUrl(string url, string service)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(service))
            return null;

        // Only process Steam service URLs
        if (!service.Equals("steam", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            // Ignore WSUS URLs like /filestreamingservice/files/...
            if (url.Contains("/filestreamingservice/", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogTrace($"Ignoring WSUS URL: {url}");
                return null;
            }

            // Extract depot ID from Steam URLs like /depot/835575/chunk/...
            var match = DepotRegex.Match(url);
            if (match.Success && uint.TryParse(match.Groups[1].Value, out var depotId))
            {
                _logger.LogTrace($"Extracted depot {depotId} from Steam URL: {url}");
                return depotId;
            }

            // No depot pattern found
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogTrace($"Error extracting depot ID from URL '{url}': {ex.Message}");
            return null;
        }
    }
}