using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SteamKit2;

namespace LancacheManager.Services;

public class SteamService
{
    private readonly ILogger<SteamService> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<uint, GameInfo> _gameCache = new();
    private readonly SemaphoreSlim _apiSemaphore = new(5); // Limit concurrent API calls
    
    // Regex patterns to extract app/depot IDs from Steam CDN URLs
    private static readonly Regex AppIdRegex = new(@"/apps?/(\d+)/", RegexOptions.Compiled);
    private static readonly Regex DepotRegex = new(@"/depot/(\d+)/", RegexOptions.Compiled);
    private static readonly Regex ChunkRegex = new(@"/chunk/([a-f0-9]+)", RegexOptions.Compiled);

    public SteamService(ILogger<SteamService> logger, IConfiguration configuration, IHttpClient httpClient)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
    }

    public class GameInfo
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = "Unknown Game";
        public string Type { get; set; } = "game";
        public string? HeaderImage { get; set; }
        public string? Description { get; set; }
        public List<string> Genres { get; set; } = new();
        public List<string> Categories { get; set; } = new();
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
            // Try to match app ID pattern
            var appMatch = AppIdRegex.Match(url);
            if (appMatch.Success && uint.TryParse(appMatch.Groups[1].Value, out var appId))
            {
                return appId;
            }

            // Try depot pattern (we can map depot to app later)
            var depotMatch = DepotRegex.Match(url);
            if (depotMatch.Success && uint.TryParse(depotMatch.Groups[1].Value, out var depotId))
            {
                // Common depot to app mappings (you can expand this)
                var depotToApp = GetDepotToAppMapping(depotId);
                if (depotToApp.HasValue)
                    return depotToApp.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extracting app ID from URL: {url}");
        }

        return null;
    }

    /// <summary>
    /// Get game information from Steam API
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
            // Try using Steam Store API (no key required)
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

            // Fallback: Try WebAPI if we have a key
            var apiKey = _configuration["Steam:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
            {
                return await GetGameInfoFromWebApi(appId, apiKey);
            }

            // Last resort: return basic info
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
            return null;
        }
        finally
        {
            _apiSemaphore.Release();
        }
    }

    private async Task<GameInfo?> GetGameInfoFromWebApi(uint appId, string apiKey)
    {
        try
        {
            using dynamic steamApps = WebAPI.GetInterface("ISteamApps", apiKey);
            steamApps.Timeout = TimeSpan.FromSeconds(5);

            var appList = steamApps.GetAppList2();
            
            foreach (KeyValue app in appList["apps"].Children)
            {
                if (app["appid"].AsUnsignedInteger() == appId)
                {
                    return new GameInfo
                    {
                        AppId = appId,
                        Name = app["name"].AsString() ?? $"App {appId}",
                        Type = "game"
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Failed to get game info from WebAPI for {appId}");
        }

        return null;
    }

    private GameInfo? ParseStoreApiResponse(string json, uint appId)
    {
        try
        {
            // Simple JSON parsing (you might want to use Newtonsoft.Json or System.Text.Json)
            using var doc = System.Text.Json.JsonDocument.Parse(json);
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

            if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var genre in genres.EnumerateArray())
                {
                    if (genre.TryGetProperty("description", out var genreDesc))
                    {
                        var genreName = genreDesc.GetString();
                        if (!string.IsNullOrEmpty(genreName))
                            gameInfo.Genres.Add(genreName);
                    }
                }
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
    private uint? GetDepotToAppMapping(uint depotId)
    {
        // Common depot mappings (expand as needed)
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
            { 381455, 570 },
            
            // Add more mappings as discovered
        };

        return mappings.TryGetValue(depotId, out var appId) ? appId : null;
    }

    /// <summary>
    /// Batch fetch game information for multiple app IDs
    /// </summary>
    public async Task<Dictionary<uint, GameInfo>> GetMultipleGameInfoAsync(IEnumerable<uint> appIds)
    {
        var results = new Dictionary<uint, GameInfo>();
        var tasks = new List<Task<(uint, GameInfo?)>>();

        foreach (var appId in appIds.Distinct())
        {
            tasks.Add(Task.Run(async () => (appId, await GetGameInfoAsync(appId))));
        }

        var completed = await Task.WhenAll(tasks);
        
        foreach (var (appId, info) in completed)
        {
            if (info != null)
            {
                results[appId] = info;
            }
        }

        return results;
    }
}