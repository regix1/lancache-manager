using System.Text.RegularExpressions;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly ILogger<LogParserService> _logger;
    
    // Regex to match your actual log format: [steam] IP ... "GET /depot/123/chunk/abc HTTP/1.1" 200 1234 ... "HIT/MISS"
    private static readonly Regex LogRegex = new(
        @"^\[(?<service>\w+)\]\s+(?<ip>[\d\.]+).*?\[(?<time>[^\]]+)\].*?""(?:GET|POST|HEAD|PUT)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+).*?""(?<cache>HIT|MISS)""",
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
            var match = LogRegex.Match(line);
            if (match.Success)
            {
                return new LogEntry
                {
                    Service = match.Groups["service"].Value.ToLower(),
                    ClientIp = match.Groups["ip"].Value,
                    Url = match.Groups["url"].Value,
                    StatusCode = int.Parse(match.Groups["status"].Value),
                    BytesServed = long.Parse(match.Groups["bytes"].Value),
                    CacheStatus = match.Groups["cache"].Value.ToUpper(),
                    Timestamp = ParseTimestamp(match.Groups["time"].Value)
                };
            }

            // Fallback for simpler format
            if (line.Contains("HIT") || line.Contains("MISS"))
            {
                return new LogEntry
                {
                    Service = ExtractService(line),
                    ClientIp = ExtractIp(line),
                    BytesServed = ExtractBytes(line),
                    StatusCode = 200,
                    CacheStatus = line.Contains("HIT") ? "HIT" : "MISS",
                    Timestamp = DateTime.UtcNow,
                    Url = ""
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace($"Failed to parse line: {ex.Message}");
        }

        return null;
    }

    private string ExtractService(string line)
    {
        if (line.StartsWith("["))
        {
            var end = line.IndexOf(']');
            if (end > 0)
            {
                return line.Substring(1, end - 1).ToLower();
            }
        }
        return DetermineServiceFromLine(line);
    }

    private string ExtractIp(string line)
    {
        var match = Regex.Match(line, @"\b(?:\d{1,3}\.){3}\d{1,3}\b");
        return match.Success ? match.Value : "unknown";
    }

    private long ExtractBytes(string line)
    {
        // Look for numbers that are likely byte counts (4+ digits)
        var matches = Regex.Matches(line, @"\b(\d{4,})\b");
        foreach (Match match in matches)
        {
            if (long.TryParse(match.Groups[1].Value, out var bytes))
            {
                // Return the first reasonable byte count found
                if (bytes > 100 && bytes < 10_000_000_000) // Between 100 bytes and 10GB
                {
                    return bytes;
                }
            }
        }
        return 0;
    }

    private string DetermineServiceFromLine(string line)
    {
        var lower = line.ToLower();
        if (lower.Contains("steam")) return "steam";
        if (lower.Contains("wsus") || lower.Contains("windowsupdate") || lower.Contains("microsoft")) return "wsus";
        if (lower.Contains("epic")) return "epic";
        if (lower.Contains("origin") || lower.Contains("ea.com")) return "origin";
        if (lower.Contains("blizzard") || lower.Contains("battle.net")) return "blizzard";
        if (lower.Contains("uplay") || lower.Contains("ubisoft")) return "uplay";
        if (lower.Contains("riot")) return "riot";
        return "other";
    }

    private DateTime ParseTimestamp(string timestamp)
    {
        try
        {
            // Handle format: 27/Apr/2025:17:48:27 -0500
            timestamp = timestamp.Replace(" -0500", "").Replace(" -0600", "").Replace(" -0400", "").Replace(" -0700", "");
            if (DateTime.TryParseExact(timestamp, 
                "dd/MMM/yyyy:HH:mm:ss", 
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var result))
            {
                return result.ToUniversalTime();
            }
        }
        catch { }
        
        return DateTime.UtcNow;
    }
}