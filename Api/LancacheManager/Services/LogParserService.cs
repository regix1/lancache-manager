using System.Text.RegularExpressions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly Subject<LogEntry> _logEntrySubject = new();
    public IObservable<LogEntry> LogEntries => _logEntrySubject.AsObservable();

    // Updated regex for parsing Lancache nginx access logs
    // Format: cacheidentifier remoteaddr - - [timestamp] "method url protocol" status bytes "referer" "useragent" "cachestatus" "host" "range"
    private static readonly Regex LogRegex = new(
        @"^(?<cache>\S+)\s+(?<ip>\S+)\s+\S+\s+\S+\s+\[(?<time>[^\]]+)\]\s+""(?<method>\w+)\s+(?<url>[^\s]+)\s+[^""]+\""\s+(?<status>\d+)\s+(?<bytes>\d+)\s+""(?<referer>[^""]*)""\s+""(?<useragent>[^""]*)""\s+""(?<cachestatus>[^""]*)""\s+""(?<host>[^""]*)""\s*(?:""(?<range>[^""]*)"")?",
        RegexOptions.Compiled);

    public LogEntry? ParseLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var match = LogRegex.Match(line);
        if (!match.Success) 
        {
            // Try alternative format without all fields
            var simpleMatch = Regex.Match(line, @"^(?<ip>\S+).*?\[(?<time>[^\]]+)\].*?""(?<method>\w+)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+)");
            if (!simpleMatch.Success)
                return null;

            // Use simple match with defaults
            match = simpleMatch;
        }

        try
        {
            var entry = new LogEntry
            {
                ClientIp = match.Groups["ip"].Success ? match.Groups["ip"].Value : "unknown",
                Method = match.Groups["method"].Success ? match.Groups["method"].Value : "GET",
                Url = match.Groups["url"].Success ? match.Groups["url"].Value : "",
                StatusCode = match.Groups["status"].Success ? int.Parse(match.Groups["status"].Value) : 0,
                BytesServed = match.Groups["bytes"].Success ? long.Parse(match.Groups["bytes"].Value) : 0,
                CacheStatus = match.Groups["cachestatus"].Success ? match.Groups["cachestatus"].Value : "MISS",
                Timestamp = ParseTimestamp(match.Groups["time"].Success ? match.Groups["time"].Value : "")
            };

            // Determine cache identifier and service from URL or first field
            if (match.Groups["cache"].Success)
            {
                entry.Service = match.Groups["cache"].Value;
            }
            else
            {
                entry.Service = DetermineService(entry.Url);
            }

            // Extract depot/download identifier based on service
            entry.DepotId = ExtractDepotId(entry.Service, entry.Url);

            _logEntrySubject.OnNext(entry);
            return entry;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing log line: {ex.Message}");
            return null;
        }
    }

    private DateTime ParseTimestamp(string timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
            return DateTime.UtcNow;

        try
        {
            // Try standard nginx format: "dd/MMM/yyyy:HH:mm:ss zzz"
            if (DateTime.TryParseExact(timestamp.Replace(" +0000", ""), 
                "dd/MMM/yyyy:HH:mm:ss", 
                System.Globalization.CultureInfo.InvariantCulture, 
                System.Globalization.DateTimeStyles.AssumeUniversal, 
                out var result))
            {
                return result.ToUniversalTime();
            }

            // Fallback to general parse
            return DateTime.Parse(timestamp).ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private string DetermineService(string url)
    {
        // Check URL patterns for different services
        if (url.Contains("steamcontent") || url.Contains("/depot/"))
            return "steam";
        if (url.Contains("origin"))
            return "origin";
        if (url.Contains("epicgames"))
            return "epic";
        if (url.Contains("uplay"))
            return "uplay";
        if (url.Contains("blizzard") || url.Contains("blzddist"))
            return "blizzard";
        if (url.Contains("riot"))
            return "riot";
        if (url.Contains("wsus") || url.Contains("windowsupdate") || url.Contains("microsoft"))
            return "wsus";
        if (url.Contains("apple"))
            return "apple";
        if (url.Contains("xboxlive"))
            return "xboxlive";
        
        return "other";
    }

    private string? ExtractDepotId(string service, string url)
    {
        try
        {
            switch (service.ToLower())
            {
                case "steam":
                    // Extract Steam depot ID from URL like /depot/123456/
                    var steamMatch = Regex.Match(url, @"/depot/(\d+)/");
                    if (steamMatch.Success)
                        return steamMatch.Groups[1].Value;
                    break;

                case "blizzard":
                    // Extract Blizzard product/version
                    var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                        return string.Join("/", parts.Take(2));
                    break;

                case "epic":
                    // Epic Games identifiers
                    var epicMatch = Regex.Match(url, @"/(\w+)/(\w+)/");
                    if (epicMatch.Success)
                        return $"{epicMatch.Groups[1].Value}/{epicMatch.Groups[2].Value}";
                    break;

                case "xboxlive":
                    // Extract Xbox app name
                    var xboxMatch = Regex.Match(url, @"/([^/_]+)_[\d\.]+_");
                    if (xboxMatch.Success)
                        return xboxMatch.Groups[1].Value;
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting depot ID: {ex.Message}");
        }

        return null;
    }
}