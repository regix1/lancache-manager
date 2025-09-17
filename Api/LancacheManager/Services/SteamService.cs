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

    // Known shared/system App IDs that should not be treated as games
    private static readonly HashSet<uint> SharedAppIds = new()
    {
        228980, // Steamworks Common Redistributables
        1007,   // Steam Client Bootstrapper
        7,      // Steam (client)
        1,      // Steam (legacy)
        1391110, // Steamworks SDK Redist
        1070560, // Steam Audio

        // Additional common redistributables and system apps
        228985, // DirectX Runtime (part of redistributables)
        228990, // Visual C++ Redistributables
        413850, // Steamworks Common Redistributables (alternate)
        4000,   // Garry's Mod Dedicated Server
        90,     // Half-Life Dedicated Server (old)
        70,     // Half-Life (legacy system entry)

        // Microsoft packages commonly downloaded with games
        1454400, // Microsoft .NET Framework
        1454401, // Microsoft Visual C++
    };

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
                // Filter out known shared/system App IDs
                if (!SharedAppIds.Contains(appId))
                {
                    return appId;
                }
                _logger.LogDebug($"Filtered out shared App ID {appId} from URL: {url}");
            }

            var depotMatch = DepotRegex.Match(url);
            if (depotMatch.Success && uint.TryParse(depotMatch.Groups[1].Value, out var depotId))
            {
                var appIdFromDepot = _depotMappingService.GetAppIdFromDepot(depotId);
                if (appIdFromDepot.HasValue)
                {
                    // Filter out shared/system App IDs from depot mappings too
                    if (!SharedAppIds.Contains(appIdFromDepot.Value) && IsValidGameAppId(appIdFromDepot.Value))
                    {
                        _logger.LogDebug($"Mapped depot {depotId} to valid game App ID {appIdFromDepot.Value}");
                        return appIdFromDepot.Value;
                    }
                    _logger.LogDebug($"Filtered out shared/invalid App ID {appIdFromDepot.Value} from depot {depotId}");
                }
                else
                {
                    _logger.LogDebug($"No mapping found for depot {depotId} in URL: {url}");
                }
            }
            
            var chunkMatch = ChunkAppIdRegex.Match(url);
            if (chunkMatch.Success && uint.TryParse(chunkMatch.Groups[1].Value, out var chunkAppId))
            {
                // Filter out known shared/system App IDs and validate range
                if (!SharedAppIds.Contains(chunkAppId) && IsValidGameAppId(chunkAppId))
                {
                    _logger.LogDebug($"Extracted App ID {chunkAppId} from chunk URL: {url}");
                    return chunkAppId;
                }
                _logger.LogDebug($"Filtered out invalid/shared App ID {chunkAppId} from chunk URL: {url}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extracting app ID from URL: {url}");
        }

        return null;
    }

    /// <summary>
    /// Validates if an App ID is likely to be a real game based on known patterns
    /// </summary>
    private static bool IsValidGameAppId(uint appId)
    {
        // App IDs below 100 are typically system/reserved
        if (appId < 100)
            return false;

        // App IDs above 2,000,000 are uncommon for games (as of 2025)
        if (appId > 2000000)
            return false;

        // Known ranges for non-games that should be filtered out
        // 228980-228999: Steamworks redistributables range
        if (appId >= 228980 && appId <= 228999)
            return false;

        // 1000-1099: Old Steam client/system range
        if (appId >= 1000 && appId <= 1099)
            return false;

        return true;
    }

    public async Task<GameInfo?> GetGameInfoAsync(uint appId)
    {
        // Don't fetch game info for known shared/system App IDs
        if (SharedAppIds.Contains(appId) || !IsValidGameAppId(appId))
        {
            _logger.LogDebug($"Skipping game info fetch for shared/invalid App ID: {appId}");
            return null;
        }

        if (_gameCache.TryGetValue(appId, out var cached))
        {
            if (DateTime.UtcNow - cached.CacheTime < TimeSpan.FromHours(24))
            {
                return cached;
            }
        }

        var appInfo = _depotMappingService.GetAppInfo(appId);
        
        await _apiSemaphore.WaitAsync();
        try
        {
            var storeUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _httpClient.GetAsync(storeUrl);
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                _logger.LogDebug($"Steam API response for {appId}: {json.Substring(0, Math.Min(200, json.Length))}...");
                
                var gameInfo = ParseStoreApiResponse(json, appId);
                
                if (gameInfo != null)
                {
                    // Ensure we have header image even if Steam didn't provide one
                    if (string.IsNullOrEmpty(gameInfo.HeaderImage))
                    {
                        gameInfo.HeaderImage = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
                    }
                    
                    // Add fallback description if missing
                    if (string.IsNullOrEmpty(gameInfo.Description))
                    {
                        gameInfo.Description = GetKnownGameDescription(appId);
                    }
                    
                    _gameCache[appId] = gameInfo;
                    _logger.LogInformation($"Fetched game info for {appId}: {gameInfo.Name}");
                    return gameInfo;
                }
                else
                {
                    _logger.LogWarning($"Steam API returned success:false or invalid data for {appId}");
                }
            }
            else
            {
                _logger.LogWarning($"Steam API returned {response.StatusCode} for app {appId}");
            }

            // Fallback with constructed values
            var fallbackInfo = new GameInfo
            {
                AppId = appId,
                Name = appInfo?.Name ?? GetKnownGameName(appId) ?? $"Steam App {appId}",
                Type = "game",
                HeaderImage = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
                Description = GetKnownGameDescription(appId)
            };
            
            _gameCache[appId] = fallbackInfo;
            return fallbackInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching game info for app {appId}");
            
            return new GameInfo
            {
                AppId = appId,
                Name = appInfo?.Name ?? GetKnownGameName(appId) ?? $"Steam App {appId}",
                Type = "game",
                HeaderImage = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
                Description = GetKnownGameDescription(appId)
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

            // Fallback to constructed URL if Steam API didn't provide header image
            if (string.IsNullOrEmpty(gameInfo.HeaderImage))
            {
                gameInfo.HeaderImage = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
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

    private string? GetKnownGameName(uint appId)
    {
        return appId switch
        {
            381210 => "Dead by Daylight",
            730 => "Counter-Strike 2",
            2767030 => "Marvel Rivals",
            570 => "Dota 2",
            440 => "Team Fortress 2",
            271590 => "Grand Theft Auto V",
            1172470 => "Apex Legends",
            _ => null
        };
    }

    private string? GetKnownGameDescription(uint appId)
    {
        return appId switch
        {
            381210 => "Death is not an escape. Dead by Daylight is a multiplayer action survival horror game where one player takes on the role of a brutal Killer and the other four play as Survivors.",
            730 => "For over two decades, Counter-Strike has offered an elite competitive experience. CS2 features state-of-the-art gameplay, all-new CS Ratings, and upgraded maps.",
            2767030 => "Marvel Rivals is a Super Hero team-based PVP shooter! Assemble an all-star squad, devise countless strategies, and fight in epic battles.",
            570 => "Every day, millions of players worldwide enter battle as one of over a hundred Dota heroes.",
            440 => "Nine distinct classes provide a broad range of tactical abilities and personalities in Team Fortress 2.",
            271590 => "Grand Theft Auto V for PC offers players the option to explore the award-winning world of Los Santos and Blaine County.",
            1172470 => "Apex Legends is the award-winning, free-to-play Hero Shooter from Respawn Entertainment.",
            _ => null
        };
    }
}