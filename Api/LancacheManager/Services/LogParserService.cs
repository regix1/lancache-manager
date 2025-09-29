using System.Text.RegularExpressions;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly ILogger<LogParserService> _logger;
    private int _failedParseCount = 0;

    // General-purpose matcher that supports both lancache logs (with service prefix)
    // and standard combined logs without the leading [service] token.
    private static readonly Regex LogLineRegex = new(
        @"^(?:\[(?<service>[^\]]+)\]\s+)?(?<ip>\S+)\s+[^\[]*\[(?<time>[^\]]+)\]\s+""(?<method>[A-Z]+)\s+(?<url>\S+)(?:\s+HTTP/(?<httpVersion>[^""\s]+))?""\s+(?<status>\d{3})\s+(?<bytes>-|\d+)(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedFieldRegex = new(@"""([^""]*)""", RegexOptions.Compiled);

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
            var match = LogLineRegex.Match(line);
            if (!match.Success)
            {
                LogParseFailure(line);
                return null;
            }

            var service = NormalizeService(match.Groups["service"].Value);
            var clientIp = match.Groups["ip"].Value;
            var url = match.Groups["url"].Value;
            var statusCode = int.Parse(match.Groups["status"].Value);

            var bytesValue = match.Groups["bytes"].Value;
            var bytesServed = bytesValue == "-" ? 0L : long.Parse(bytesValue);

            var timestamp = ParseTimestamp(match.Groups["time"].Value);
            var cacheStatus = ResolveCacheStatus(match.Groups["rest"].Value);

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

    private void LogParseFailure(string line)
    {
        if (_failedParseCount < 100)
        {
            _logger.LogWarning($"Failed to parse line #{_failedParseCount}: {TruncateLineForLog(line)}");
        }

        _failedParseCount++;

        if (_failedParseCount % 10000 == 0)
        {
            _logger.LogError($"CRITICAL: {_failedParseCount} lines have failed to parse and been lost!");
        }
    }

    private static string NormalizeService(string rawService)
    {
        if (string.IsNullOrWhiteSpace(rawService))
        {
            return "unknown";
        }

        var lowered = rawService.Trim().ToLowerInvariant();
        return System.Net.IPAddress.TryParse(lowered, out _) ? "unknown" : lowered;
    }

    private static string TruncateLineForLog(string line)
    {
        const int maxLength = 200;
        return line.Length <= maxLength ? line : $"{line.Substring(0, maxLength)}...";
    }

    private string ResolveCacheStatus(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return "UNKNOWN";
        }

        var matches = QuotedFieldRegex.Matches(rest);
        if (matches.Count >= 3)
        {
            var raw = matches[2].Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(raw) && raw != "-")
            {
                return raw.ToUpperInvariant();
            }
        }

        return "UNKNOWN";
    }

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
