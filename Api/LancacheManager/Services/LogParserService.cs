using System.Text.RegularExpressions;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly ILogger<LogParserService> _logger;
    private static readonly Regex LogRegex = new(
        @"^\[?(?<service>\w+)\]?\s+(?<ip>\d+\.\d+\.\d+\.\d+).*?\[(?<time>[^\]]+)\].*?""(?:GET|POST)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+).*?""(?<cache>HIT|MISS)""",
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
            if (!match.Success)
            {
                // Try simpler pattern
                var simpleMatch = Regex.Match(line, @"(?<ip>\d+\.\d+\.\d+\.\d+).*?\[(?<time>[^\]]+)\].*?(?<status>\d+)\s+(?<bytes>\d+)");
                if (!simpleMatch.Success) return null;
                
                return new LogEntry
                {
                    Service = DetermineService(line),
                    ClientIp = simpleMatch.Groups["ip"].Value,
                    BytesServed = long.Parse(simpleMatch.Groups["bytes"].Value),
                    StatusCode = int.Parse(simpleMatch.Groups["status"].Value),
                    CacheStatus = line.Contains("HIT") ? "HIT" : "MISS",
                    Timestamp = ParseTimestamp(simpleMatch.Groups["time"].Value)
                };
            }

            return new LogEntry
            {
                Service = match.Groups["service"].Value.ToLower(),
                ClientIp = match.Groups["ip"].Value,
                Url = match.Groups["url"].Value,
                StatusCode = int.Parse(match.Groups["status"].Value),
                BytesServed = long.Parse(match.Groups["bytes"].Value),
                CacheStatus = match.Groups["cache"].Value,
                Timestamp = ParseTimestamp(match.Groups["time"].Value)
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to parse line: {ex.Message}");
            return null;
        }
    }

    private string DetermineService(string line)
    {
        var lowerLine = line.ToLower();
        if (lowerLine.Contains("steam")) return "steam";
        if (lowerLine.Contains("epic")) return "epic";
        if (lowerLine.Contains("origin") || lowerLine.Contains("ea.com")) return "origin";
        if (lowerLine.Contains("blizzard") || lowerLine.Contains("battle.net")) return "blizzard";
        if (lowerLine.Contains("uplay") || lowerLine.Contains("ubisoft")) return "uplay";
        if (lowerLine.Contains("wsus") || lowerLine.Contains("windowsupdate")) return "wsus";
        if (lowerLine.Contains("apple")) return "apple";
        return "other";
    }

    private DateTime ParseTimestamp(string timestamp)
    {
        try
        {
            if (DateTime.TryParseExact(timestamp.Replace(" -0500", ""), 
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