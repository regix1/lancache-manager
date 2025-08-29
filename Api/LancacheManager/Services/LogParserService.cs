using System.Text.RegularExpressions;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly ILogger<LogParserService> _logger;
    
    // Updated regex to match your actual log format:
    // [service] IP / - - - [timestamp] "METHOD URL HTTP/version" status bytes "-" "user-agent" "HIT/MISS" "domain" "-"
    private static readonly Regex LogRegex = new(
        @"^\[(?<service>\w+)\]\s+(?<ip>[\d\.]+).*?\[(?<time>[^\]]+)\].*?""(?:GET|POST|HEAD|PUT|OPTIONS|DELETE|PATCH)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+).*?""(?<cache>HIT|MISS|EXPIRED|UPDATING|STALE|BYPASS|REVALIDATED)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public LogParserService(ILogger<LogParserService> logger)
    {
        _logger = logger;
    }

    public LogEntry? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            // Skip heartbeat requests
            if (line.Contains("lancache-heartbeat"))
            {
                return null;
            }

            var match = LogRegex.Match(line);
            if (match.Success)
            {
                var service = match.Groups["service"].Value.ToLower();
                var clientIp = match.Groups["ip"].Value;
                var url = match.Groups["url"].Value;
                var statusCode = int.Parse(match.Groups["status"].Value);
                var bytesServed = long.Parse(match.Groups["bytes"].Value);
                var cacheStatus = match.Groups["cache"].Value.ToUpper();
                var timestamp = ParseTimestamp(match.Groups["time"].Value);

                // Skip localhost entries unless they're actual downloads
                if (clientIp == "127.0.0.1" && bytesServed < 1000)
                {
                    return null;
                }

                _logger.LogTrace($"Parsed: {service} {clientIp} {bytesServed} bytes {cacheStatus}");

                return new LogEntry
                {
                    Service = service,
                    ClientIp = clientIp,
                    Url = url,
                    StatusCode = statusCode,
                    BytesServed = bytesServed,
                    CacheStatus = cacheStatus,
                    Timestamp = timestamp
                };
            }
            else
            {
                // Log first few failures for debugging
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace($"Failed to parse line: {line.Substring(0, Math.Min(100, line.Length))}...");
                }
            }
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
}