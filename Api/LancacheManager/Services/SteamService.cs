using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace LancacheManager.Services;

public class SteamService
{
    private readonly ILogger<SteamService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, GameInfo> _gameCache = new();
    private readonly SemaphoreSlim _apiSemaphore = new(5); // Limit concurrent API calls
    
    // Regex patterns to extract app/depot IDs from Steam CDN URLs
    private static readonly Regex AppIdRegex = new(@"/apps?/(\d+)/", RegexOptions.Compiled);
    private static readonly Regex DepotRegex = new(@"/depot/(\d+)/", RegexOptions.Compiled);

    public SteamService(ILogger<SteamService> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public class GameInfo
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = "Unknown Game";
        public string Type { get; set; } = "game";
        public string? HeaderImage { get; set; }
        public string? Description { get; set; }
        public bool IsFree { get; set; }
        public DateTime CacheTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Extract Steam app ID from a download URL
    /// </summary>
    public uint? ExtractAppIdFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        try
        {
            // Try to match app ID pattern like /app/730/ or /apps/730/
            var appMatch = AppIdRegex.Match(url);
            if (appMatch.Success && uint.TryParse(appMatch.Groups[1].Value, out var appId))
            {
                return appId;
            }

            // Try depot pattern
            var depotMatch = DepotRegex.Match(url);
            if (depotMatch.Success && uint.TryParse(depotMatch.Groups[1].Value, out var depotId))
            {
                // Map known depot IDs to app IDs
                var appIdFromDepot = GetAppIdFromDepot(depotId);
                if (appIdFromDepot.HasValue)
                    return appIdFromDepot.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extracting app ID from URL: {url}");
        }

        return null;
    }

    /// <summary>
    /// Get game information from Steam Store API (no key required)
    /// </summary>
    public async Task<GameInfo?> GetGameInfoAsync(uint appId)
    {
        // Check cache first (cache for 24 hours)
        if (_gameCache.TryGetValue(appId, out var cached))
        {
            if (DateTime.UtcNow - cached.CacheTime < TimeSpan.FromHours(24))
            {
                return cached;
            }
        }

        await _apiSemaphore.WaitAsync();
        try
        {
            // Use Steam Store API (no key required)
            var storeUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _httpClient.GetAsync(storeUrl);
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var gameInfo = ParseStoreApiResponse(json, appId);
                
                if (gameInfo != null)
                {
                    _gameCache[appId] = gameInfo;
                    _logger.LogInformation($"Fetched game info for {appId}: {gameInfo.Name}");
                    return gameInfo;
                }
            }

            // If we can't get the info, return basic info with app ID
            var basicInfo = new GameInfo
            {
                AppId = appId,
                Name = $"Steam App {appId}",
                Type = "unknown"
            };
            
            _gameCache[appId] = basicInfo;
            return basicInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching game info for app {appId}");
            
            // Return basic info on error
            return new GameInfo
            {
                AppId = appId,
                Name = $"Steam App {appId}",
                Type = "unknown"
            };
        }
        finally
        {
            _apiSemaphore.Release();
        }
    }

    private GameInfo? ParseStoreApiResponse(string json, uint appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (!root.TryGetProperty(appId.ToString(), out var appData))
                return null;

            if (!appData.TryGetProperty("success", out var success) || !success.GetBoolean())
                return null;

            if (!appData.TryGetProperty("data", out var data))
                return null;

            var gameInfo = new GameInfo
            {
                AppId = appId,
                Name = data.TryGetProperty("name", out var name) ? name.GetString() ?? $"App {appId}" : $"App {appId}",
                Type = data.TryGetProperty("type", out var type) ? type.GetString() ?? "game" : "game",
                IsFree = data.TryGetProperty("is_free", out var isFree) && isFree.GetBoolean()
            };

            if (data.TryGetProperty("header_image", out var headerImage))
            {
                gameInfo.HeaderImage = headerImage.GetString();
            }

            if (data.TryGetProperty("short_description", out var desc))
            {
                gameInfo.Description = desc.GetString();
            }

            return gameInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error parsing Store API response for {appId}");
            return null;
        }
    }

    /// <summary>
    /// Map known depot IDs to app IDs
    /// </summary>
    private uint? GetAppIdFromDepot(uint depotId)
    {
        // Common depot to app mappings
        var mappings = new Dictionary<uint, uint>
        {
            // Counter-Strike 2
            { 2275530, 730 },
            { 2275531, 730 },
            { 2275532, 730 },
            { 2275533, 730 },
            
            // Team Fortress 2
            { 441, 440 },
            { 442, 440 },
            
            // Dota 2
            { 571, 570 },
            { 381451, 570 },
            { 381452, 570 },
            { 381453, 570 },
            { 381454, 570 },
            
            // Portal 2
            { 620, 620 },
            { 621, 620 },
            
            // Left 4 Dead 2
            { 550, 550 },
            { 551, 550 },
            
            // Add more mappings as needed
        };

        return mappings.TryGetValue(depotId, out var appId) ? appId : null;
    }
}