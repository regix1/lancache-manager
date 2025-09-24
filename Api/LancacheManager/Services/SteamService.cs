using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;

namespace LancacheManager.Services;

/// <summary>
/// Unified Steam service that combines app info handling, depot mapping, and game information retrieval
/// Uses real Steam API data with proper depot-to-app mapping instead of URL pattern guessing
/// </summary>
public class SteamService : IHostedService, IDisposable
{
    private readonly ILogger<SteamService> _logger;
    private readonly HttpClient _httpClient;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _apiSemaphore = new(5);
    private readonly Timer? _refreshTimer;

    // Caches for performance
    private readonly ConcurrentDictionary<uint, GameInfo> _gameCache = new();
    private readonly ConcurrentDictionary<uint, uint> _depotToAppCache = new();
    private readonly ConcurrentDictionary<uint, SteamAppInfo> _appInfoCache = new();

    // Steam API data
    private Dictionary<uint, SteamAppInfo> _steamApps = new();
    private Dictionary<uint, HashSet<uint>> _depotMappings = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private bool _isReady = false;

    // Regex patterns for depot extraction
    private static readonly Regex DepotRegex = new(@"/depot/(\d+)/", RegexOptions.Compiled);

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

    public class SteamAppInfo
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "game";
        public string? HeaderImage { get; set; }
        public string? Description { get; set; }
        public DateTime CacheTime { get; set; } = DateTime.UtcNow;
        public List<uint> Depots { get; set; } = new();
    }

    public SteamService(
        ILogger<SteamService> logger,
        HttpClient httpClient,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _scopeFactory = scopeFactory;

        // Refresh mappings every 6 hours
        _refreshTimer = new Timer(async _ => await RefreshMappingsAsync(), null, TimeSpan.Zero, TimeSpan.FromHours(6));
    }

    #region IHostedService Implementation

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SteamService...");
        await RefreshMappingsAsync();
        _logger.LogInformation("SteamService started successfully");
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SteamService...");
        _refreshTimer?.Dispose();
        return Task.CompletedTask;
    }

    #endregion

    #region Steam API Data Management

    /// <summary>
    /// Refresh depot mappings and app info from Steam API
    /// </summary>
    private async Task RefreshMappingsAsync()
    {
        try
        {
            _logger.LogInformation("Refreshing Steam depot mappings and app info...");

            // Load from database first
            await LoadFromDatabaseAsync();

            // Check if we need to refresh from Steam API (daily refresh)
            if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromDays(1) && _steamApps.Count > 0)
            {
                var depotCount = _depotMappings.Count;
                var mappingCount = _depotMappings.Sum(kvp => kvp.Value.Count);
                _logger.LogInformation(
                    "Using cached Steam data ({AppCount} apps, {DepotCount} depots, {MappingCount} depot mappings)",
                    _steamApps.Count,
                    depotCount,
                    mappingCount);
                _isReady = true;
                return;
            }

            // Refresh from Steam API
            await RefreshFromSteamApiAsync();
            await SaveToDatabaseAsync();

            _lastRefresh = DateTime.UtcNow;
            _isReady = true;

            var totalMappings = _depotMappings.Sum(kvp => kvp.Value.Count);
            _logger.LogInformation(
                "Steam data refreshed: {AppCount} apps, {DepotCount} depots, {MappingCount} depot mappings",
                _steamApps.Count,
                _depotMappings.Count,
                totalMappings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing Steam mappings");

            // Try to load from database as fallback
            if (_steamApps.Count == 0)
            {
                await LoadFromDatabaseAsync();
            }

            _isReady = _steamApps.Count > 0;
        }
    }

    private async Task RefreshFromSteamApiAsync()
    {
        try
        {
            // Get app list from Steam API
            var appListUrl = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
            var response = await _httpClient.GetAsync(appListUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning($"Failed to get Steam app list: {response.StatusCode}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("applist", out var applist) ||
                !applist.TryGetProperty("apps", out var apps))
            {
                _logger.LogWarning("Invalid Steam app list response format");
                return;
            }

            var newSteamApps = new Dictionary<uint, SteamAppInfo>();
            var processedCount = 0;

            foreach (var app in apps.EnumerateArray())
            {
                if (app.TryGetProperty("appid", out var appIdElement) &&
                    app.TryGetProperty("name", out var nameElement))
                {
                    var appId = appIdElement.GetUInt32();
                    var name = nameElement.GetString();

                    if (!string.IsNullOrWhiteSpace(name) && IsValidGameAppId(appId))
                    {
                        newSteamApps[appId] = new SteamAppInfo
                        {
                            AppId = appId,
                            Name = name,
                            Type = "game",
                            CacheTime = DateTime.UtcNow
                        };
                        processedCount++;
                    }
                }
            }

            _steamApps = newSteamApps;
            _logger.LogInformation($"Loaded {processedCount} Steam apps from API");

            // Update depot mappings (this would require additional Steam API calls or database)
            await RefreshDepotMappingsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing from Steam API");
        }
    }

    private async Task RefreshDepotMappingsAsync()
    {
        // For now, we'll rely on database stored mappings and real-time depot extraction
        // In a full implementation, this would query Steam's depot information
        _logger.LogInformation("Depot mappings refresh completed (using database and real-time extraction)");
    }

    /// <summary>
    /// Validates if an App ID is likely to be a real game
    /// </summary>
    private static bool IsValidGameAppId(uint appId)
    {
        // App IDs below 100 are typically system/reserved
        if (appId < 100) return false;

        // App IDs above 3,000,000 are uncommon for games
        if (appId > 3000000) return false;

        // Known system app ranges
        if (appId >= 228980 && appId <= 228999) return false; // Steamworks redistributables
        if (appId >= 1000 && appId <= 1099) return false; // Steam client/system

        return true;
    }

    #endregion

    #region Database Operations

    private async Task LoadFromDatabaseAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Load depot mappings from database and bucket by depot
            var depotMappings = await dbContext.SteamDepotMappings.ToListAsync();

            var grouped = new Dictionary<uint, HashSet<uint>>();
            foreach (var mapping in depotMappings)
            {
                if (!grouped.TryGetValue(mapping.DepotId, out var set))
                {
                    set = new HashSet<uint>();
                    grouped[mapping.DepotId] = set;
                }

                set.Add(mapping.AppId);
            }

            _depotMappings = grouped;
            _depotToAppCache.Clear();

            foreach (var kvp in _depotMappings)
            {
                var depotId = kvp.Key;
                if (kvp.Value.Count == 0)
                {
                    continue;
                }

                var representativeApp = kvp.Value.Min();
                _depotToAppCache[depotId] = representativeApp;
            }

            var depotCount = _depotMappings.Count;
            var mappingCount = _depotMappings.Sum(kvp => kvp.Value.Count);
            _logger.LogInformation(
                "Loaded {DepotCount} depots with {MappingCount} mappings from database",
                depotCount,
                mappingCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading depot mappings from database");
        }
    }

    private async Task SaveToDatabaseAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Save depot mappings (if new entries were discovered)
            var existingMappings = await dbContext.SteamDepotMappings
                .ToDictionaryAsync(m => (m.DepotId, m.AppId));

            var added = 0;

            foreach (var (depotId, appIds) in _depotMappings)
            {
                foreach (var appId in appIds)
                {
                    if (existingMappings.ContainsKey((depotId, appId)))
                    {
                        continue;
                    }

                    var newMapping = new SteamDepotMapping
                    {
                        DepotId = depotId,
                        AppId = appId,
                        Source = "steam-api",
                        Confidence = 90,
                        DiscoveredAt = DateTime.UtcNow
                    };

                    dbContext.SteamDepotMappings.Add(newMapping);
                    existingMappings[(depotId, appId)] = newMapping;
                    added++;
                }
            }

            if (added > 0)
            {
                await dbContext.SaveChangesAsync();
                _logger.LogInformation("Saved {Count} depot mappings to database", added);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving depot mappings to database");
        }
    }

    #endregion

    #region Public API Methods

    /// <summary>
    /// Extract depot ID from Steam URL
    /// </summary>
    public uint? ExtractDepotIdFromUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        try
        {
            var match = DepotRegex.Match(url);
            if (match.Success && uint.TryParse(match.Groups[1].Value, out var depotId))
            {
                _logger.LogDebug($"Extracted depot {depotId} from URL: {url}");
                return depotId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error extracting depot ID from URL: {url}");
        }

        return null;
    }

    /// <summary>
    /// Extract app ID from URL using depot mapping
    /// </summary>
    public uint? ExtractAppIdFromUrl(string url)
    {
        var depotId = ExtractDepotIdFromUrl(url);
        if (!depotId.HasValue) return null;

        return GetAppIdFromDepot(depotId.Value);
    }

    /// <summary>
    /// Get app ID from depot using real Steam API relationships
    /// </summary>
    public uint? GetAppIdFromDepot(uint depotId)
    {
        try
        {
            // Check cache first
            if (_depotToAppCache.TryGetValue(depotId, out var cachedAppId))
            {
                _logger.LogDebug($"Cache hit: depot {depotId} -> app {cachedAppId}");
                return cachedAppId;
            }

            // Check main mappings
            if (_depotMappings.TryGetValue(depotId, out var appIds) && appIds.Count > 0)
            {
                var selectedAppId = appIds.Min();
                _depotToAppCache[depotId] = selectedAppId;
                _logger.LogDebug(
                    "Mapped depot {DepotId} to app {AppId} (total apps mapped={AppCount})",
                    depotId,
                    selectedAppId,
                    appIds.Count);
                return selectedAppId;
            }

            _logger.LogDebug($"No mapping found for depot {depotId}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting app ID from depot {depotId}");
            return null;
        }
    }

    /// <summary>
    /// Get all app IDs associated with a depot.
    /// </summary>
    public IReadOnlyCollection<uint> GetAppIdsForDepot(uint depotId)
    {
        if (_depotMappings.TryGetValue(depotId, out var appIds) && appIds.Count > 0)
        {
            return appIds.ToArray();
        }

        return Array.Empty<uint>();
    }

    /// <summary>
    /// Get game information using real Steam API data
    /// </summary>
    public async Task<GameInfo?> GetGameInfoAsync(uint appId)
    {
        try
        {
            // Check cache first
            if (_gameCache.TryGetValue(appId, out var cached))
            {
                if (DateTime.UtcNow - cached.CacheTime < TimeSpan.FromHours(24))
                {
                    return cached;
                }
            }

            // Check if we have basic app info
            if (_steamApps.TryGetValue(appId, out var steamApp))
            {
                // Try to get detailed info from Steam Store API
                var detailedInfo = await GetDetailedGameInfoAsync(appId, steamApp.Name);
                if (detailedInfo != null)
                {
                    _gameCache[appId] = detailedInfo;
                    return detailedInfo;
                }
            }

            // Fallback: try to get info directly from Steam Store API
            var fallbackInfo = await GetDetailedGameInfoAsync(appId);
            if (fallbackInfo != null)
            {
                _gameCache[appId] = fallbackInfo;
                return fallbackInfo;
            }

            _logger.LogDebug($"No game info found for app {appId}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting game info for app {appId}");
            return null;
        }
    }

    private async Task<GameInfo?> GetDetailedGameInfoAsync(uint appId, string? knownName = null)
    {
        await _apiSemaphore.WaitAsync();
        try
        {
            var storeUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _httpClient.GetAsync(storeUrl);

            if (!response.IsSuccessStatusCode)
            {
                return CreateFallbackGameInfo(appId, knownName);
            }

            var json = await response.Content.ReadAsStringAsync();
            return ParseStoreApiResponse(json, appId, knownName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching detailed game info for app {appId}");
            return CreateFallbackGameInfo(appId, knownName);
        }
        finally
        {
            _apiSemaphore.Release();
        }
    }

    private GameInfo? ParseStoreApiResponse(string json, uint appId, string? knownName = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty(appId.ToString(), out var appData) ||
                !appData.TryGetProperty("success", out var success) ||
                !success.GetBoolean() ||
                !appData.TryGetProperty("data", out var data))
            {
                return CreateFallbackGameInfo(appId, knownName);
            }

            var gameInfo = new GameInfo
            {
                AppId = appId,
                Name = data.TryGetProperty("name", out var name) ? name.GetString() ?? knownName ?? $"App {appId}" : knownName ?? $"App {appId}",
                Type = data.TryGetProperty("type", out var type) ? type.GetString() ?? "game" : "game",
                IsFree = data.TryGetProperty("is_free", out var isFree) && isFree.GetBoolean(),
                HeaderImage = data.TryGetProperty("header_image", out var headerImage) ? headerImage.GetString() : $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
                Description = data.TryGetProperty("short_description", out var desc) ? desc.GetString() : null,
                CacheTime = DateTime.UtcNow
            };

            return gameInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error parsing Store API response for {appId}");
            return CreateFallbackGameInfo(appId, knownName);
        }
    }

    private static GameInfo CreateFallbackGameInfo(uint appId, string? knownName = null)
    {
        return new GameInfo
        {
            AppId = appId,
            Name = knownName ?? $"Steam App {appId}",
            Type = "game",
            HeaderImage = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            Description = null,
            CacheTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Get multiple game info entries efficiently
    /// </summary>
    public async Task<Dictionary<uint, GameInfo>> GetMultipleGameInfoAsync(IEnumerable<uint> appIds)
    {
        var result = new Dictionary<uint, GameInfo>();
        var tasks = appIds.Select(async appId =>
        {
            var gameInfo = await GetGameInfoAsync(appId);
            if (gameInfo != null)
            {
                result[appId] = gameInfo;
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>
    /// Get all available games from Steam API
    /// </summary>
    public Dictionary<uint, GameInfo> GetAvailableGames()
    {
        var result = new Dictionary<uint, GameInfo>();

        foreach (var steamApp in _steamApps.Values)
        {
            var gameInfo = new GameInfo
            {
                AppId = steamApp.AppId,
                Name = steamApp.Name,
                Type = steamApp.Type,
                HeaderImage = steamApp.HeaderImage ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{steamApp.AppId}/header.jpg",
                Description = steamApp.Description,
                CacheTime = steamApp.CacheTime
            };
            result[steamApp.AppId] = gameInfo;
        }

        return result;
    }

    /// <summary>
    /// Clear all caches
    /// </summary>
    public void ClearCache()
    {
        _gameCache.Clear();
        _depotToAppCache.Clear();
        _appInfoCache.Clear();
        _logger.LogInformation("Cleared all Steam service caches");
    }

    /// <summary>
    /// Get service statistics
    /// </summary>
    public (int CachedGames, int SteamApps, bool SessionReady) GetStats()
    {
        return (_gameCache.Count, _steamApps.Count, _isReady);
    }

    /// <summary>
    /// Check if Steam API session is ready
    /// </summary>
    public bool IsReady => _isReady;

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _apiSemaphore?.Dispose();
    }

    #endregion
}
