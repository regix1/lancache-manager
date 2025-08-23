using System.Text.RegularExpressions;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using LancacheManager.Models;

namespace LancacheManager.Services;

public class LogParserService
{
    private readonly Subject<LogEntry> _logEntrySubject = new();
    public IObservable<LogEntry> LogEntries => _logEntrySubject.AsObservable();

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
            var simpleMatch = Regex.Match(line, @"^(?<ip>\S+).*?\[(?<time>[^\]]+)\].*?""(?<method>\w+)\s+(?<url>[^\s]+).*?""\s+(?<status>\d+)\s+(?<bytes>\d+)");
            if (!simpleMatch.Success)
                return null;

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

            var cacheIdentifier = match.Groups["cache"].Success ? match.Groups["cache"].Value : "";
            var host = match.Groups["host"].Success ? match.Groups["host"].Value : "";
            
            DetermineServiceAndApp(entry, cacheIdentifier, host);
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

    private void DetermineServiceAndApp(LogEntry entry, string cacheIdentifier, string host)
    {
        var url = entry.Url.ToLower();
        
        if (!string.IsNullOrEmpty(cacheIdentifier))
        {
            entry.Service = cacheIdentifier.ToLower();
        }
        else if (!string.IsNullOrEmpty(host))
        {
            entry.Service = ExtractServiceFromHost(host);
        }
        else
        {
            entry.Service = DetermineServiceFromUrl(url);
        }
    }

    private string ExtractServiceFromHost(string host)
    {
        var hostLower = host.ToLower();
        
        // Check for known service patterns in hostname
        if (hostLower.Contains("steam") || hostLower.Contains("valve"))
            return "steam";
        if (hostLower.Contains("origin") || hostLower.Contains("ea.com"))
            return "origin";
        if (hostLower.Contains("epic") || hostLower.Contains("unrealengine"))
            return "epic";
        if (hostLower.Contains("blizzard") || hostLower.Contains("battle.net") || hostLower.Contains("blzddist"))
            return "blizzard";
        if (hostLower.Contains("riot") || hostLower.Contains("riotgames"))
            return "riot";
        if (hostLower.Contains("uplay") || hostLower.Contains("ubisoft"))
            return "uplay";
        if (hostLower.Contains("apple") || hostLower.Contains("itunes"))
            return "apple";
        if (hostLower.Contains("microsoft") || hostLower.Contains("windowsupdate") || hostLower.Contains("update.microsoft"))
            return "wsus";
        if (hostLower.Contains("xbox") || hostLower.Contains("xboxlive"))
            return "xboxlive";
        if (hostLower.Contains("sony") || hostLower.Contains("playstation"))
            return "playstation";
        if (hostLower.Contains("nintendo"))
            return "nintendo";
        
        // Extract first meaningful part of hostname
        var parts = hostLower.Split('.');
        if (parts.Length > 0)
        {
            var serviceName = parts[0];
            // Remove common prefixes
            serviceName = Regex.Replace(serviceName, @"^(cdn|download|content|cache|update)", "");
            if (!string.IsNullOrWhiteSpace(serviceName))
                return serviceName;
        }
        
        return "other";
    }

    private string DetermineServiceFromUrl(string url)
    {
        if (url.Contains("steamcontent") || url.Contains("/depot/") || url.Contains("steampowered"))
            return "steam";
        if (url.Contains("origin") || url.Contains("ea.com"))
            return "origin";
        if (url.Contains("epicgames") || url.Contains("unrealengine"))
            return "epic";
        if (url.Contains("uplay") || url.Contains("ubisoft"))
            return "uplay";
        if (url.Contains("blizzard") || url.Contains("blzddist") || url.Contains("battle.net"))
            return "blizzard";
        if (url.Contains("riot") || url.Contains("riotgames"))
            return "riot";
        if (url.Contains("wsus") || url.Contains("windowsupdate") || url.Contains("microsoft"))
            return "wsus";
        if (url.Contains("apple") || url.Contains("itunes"))
            return "apple";
        if (url.Contains("xboxlive") || url.Contains("xbox"))
            return "xboxlive";
        if (url.Contains("sony") || url.Contains("playstation"))
            return "playstation";
        if (url.Contains("nintendo"))
            return "nintendo";
        
        return "other";
    }

    private DateTime ParseTimestamp(string timestamp)
    {
        if (string.IsNullOrEmpty(timestamp))
            return DateTime.UtcNow;

        try
        {
            if (DateTime.TryParseExact(timestamp.Replace(" +0000", ""), 
                "dd/MMM/yyyy:HH:mm:ss", 
                System.Globalization.CultureInfo.InvariantCulture, 
                System.Globalization.DateTimeStyles.AssumeUniversal, 
                out var result))
            {
                return result.ToUniversalTime();
            }

            return DateTime.Parse(timestamp).ToUniversalTime();
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private string? ExtractDepotId(string service, string url)
    {
        try
        {
            switch (service.ToLower())
            {
                case "steam":
                    // Multiple patterns for Steam
                    var patterns = new[]
                    {
                        @"/depot/(\d+)/",
                        @"/(\d{3,7})/",
                        @"app[_/](\d+)",
                        @"depot[_/](\d+)"
                    };
                    
                    foreach (var pattern in patterns)
                    {
                        var match = Regex.Match(url, pattern);
                        if (match.Success)
                            return match.Groups[1].Value;
                    }
                    break;

                case "epic":
                    // Epic Games patterns
                    var epicMatch = Regex.Match(url, @"/([^/]+)/([^/]+)/");
                    if (epicMatch.Success)
                        return epicMatch.Groups[1].Value;
                    break;

                case "blizzard":
                    // Blizzard patterns
                    var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                        return parts[0];
                    break;

                case "origin":
                    // Origin/EA patterns
                    var originMatch = Regex.Match(url, @"/([^/]+)/(content|game)/");
                    if (originMatch.Success)
                        return originMatch.Groups[1].Value;
                    break;

                case "uplay":
                    // Ubisoft patterns
                    var uplayMatch = Regex.Match(url, @"/([^/]+)/content/");
                    if (uplayMatch.Success)
                        return uplayMatch.Groups[1].Value;
                    break;

                case "xboxlive":
                    // Xbox patterns
                    var xboxMatch = Regex.Match(url, @"/([^/_]+)_[\d\.]+_");
                    if (xboxMatch.Success)
                        return xboxMatch.Groups[1].Value;
                    break;

                default:
                    // Generic pattern extraction
                    var genericMatch = Regex.Match(url, @"/([^/]+)/(game|content|app|depot)/");
                    if (genericMatch.Success)
                        return genericMatch.Groups[1].Value;
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