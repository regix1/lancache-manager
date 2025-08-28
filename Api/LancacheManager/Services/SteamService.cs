using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace LancacheManager.Services;

public class SteamService
{
    private readonly ILogger<SteamService> _logger;
    private readonly HttpClient _httpClient;
    private readonly SteamDepotMappingService _depotMappingService;
    private readonly ConcurrentDictionary<uint, GameInfo> _gameCache = new();
    private readonly SemaphoreSlim _apiSemaphore = new(5);
    
    private static readonly Regex AppIdRegex = new(@"/apps?/(\d+)/", RegexOptions.Compiled);
    private static readonly Regex DepotRegex = new(@"/depot/(\d+)/", RegexOptions.Compiled);
    private static readonly Regex ChunkAppIdRegex = new(@"/(\d{3,7})_depot_\d+", RegexOptions.Compiled);

    public SteamService(
        ILogger<SteamService> logger, 
        HttpClient httpClient,
        SteamDepotMappingService depotMappingService)
    {
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _depotMappingService = depotMappingService;
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

    public uint? ExtractAppIdFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        try
        {
            var appMatch = AppIdRegex.Match(url);
            if (appMatch.Success && uint.TryParse(appMatch.Groups[1].Value, out var appId))
            {
                return appId;
            }

            var depotMatch = DepotRegex.Match(url);
            if (depotMatch.Success && uint.TryParse(depotMatch.Groups[1].Value, out var depotId))
            {
                var appIdFromDepot = _depotMappingService.GetAppIdFromDepot(depotId);
                if (appIdFromDepot.HasValue)
                {
                    _logger.LogDebug($"Mapped depot {depotId} to app {appIdFromDepot.Value}");
                    return appIdFromDepot.Value;
                }
                
                _logger.LogDebug($"No mapping found for depot {depotId}");
            }
            
            var chunkMatch = ChunkAppIdRegex.Match(url);
            if (chunkMatch.Success && uint.TryParse(chunkMatch.Groups[1].Value, out var chunkAppId))
            {
                return chunkAppId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extracting app ID from URL: {url}");
        }

        return null;
    }

    public async Task<GameInfo?> GetGameInfoAsync(uint appId)
    {
        if (_gameCache.TryGetValue(appId, out var cached))
        {
            if (DateTime.UtcNow - cached.CacheTime < TimeSpan.FromHours(24))
            {
                return cached;
            }
        }

        var appInfo = _depotMappingService.GetAppInfo(appId);
        if (appInfo != null && !string.IsNullOrEmpty(appInfo.Name) && appInfo.Name != "Unknown")
        {
            var gameInfo = new GameInfo
            {
                AppId = appId,
                Name = appInfo.Name,
                Type = "game"
            };
            _gameCache[appId] = gameInfo;
            return gameInfo;
        }

        await _apiSemaphore.WaitAsync();
        try
        {
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
                    
                    // If we successfully identified a game from a depot URL, save this mapping
                    // This will be picked up by the mapping service's analyze process
                    
                    return gameInfo;
                }
            }

            var basicInfo = new GameInfo
            {
                AppId = appId,
                Name = appInfo?.Name ?? $"Steam App {appId}",
                Type = "unknown"
            };
            
            _gameCache[appId] = basicInfo;
            return basicInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching game info for app {appId}");
            
            return new GameInfo
            {
                AppId = appId,
                Name = appInfo?.Name ?? $"Steam App {appId}",
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
}