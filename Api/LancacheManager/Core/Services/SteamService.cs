using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Extensions;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// On-demand Steam game information retrieval (Store API) and depot→app owner lookups.
/// </summary>
public class SteamService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SteamService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _apiSemaphore = new(5);

    // Caches for performance
    private readonly ConcurrentDictionary<long, GameInfo> _gameCache = new();

    public class GameInfo
    {
        public long AppId { get; set; }
        public string Name { get; set; } = "Unknown Game";
        public string Type { get; set; } = "game";
        public string? HeaderImage { get; set; }
        public string? Description { get; set; }
        public bool IsFree { get; set; }
        public DateTime CacheTime { get; set; } = DateTime.UtcNow;
    }

    public SteamService(
        HttpClient httpClient,
        ILogger<SteamService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    #region Public API Methods

    /// <summary>
    /// Get all app IDs associated with a depot.
    /// Queries database directly for authoritative data.
    /// </summary>
    public IReadOnlyCollection<long> GetAppIdsForDepot(long depotId)
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            // Only return owner apps - no fallback/guessing
            var appIds = scopedDb.DbContext.SteamDepotMappings
                .AsNoTracking()
                .Where(m => m.DepotId == depotId && m.IsOwner)  // Owner apps only
                .Select(m => (long)m.AppId)
                .ToList();

            return appIds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database for depot {DepotId}", depotId);
            return Array.Empty<long>();
        }
    }

    /// <summary>
    /// Get game information using real Steam API data
    /// </summary>
    public async Task<GameInfo?> GetGameInfoAsync(long appId)
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

            var gameInfo = await GetDetailedGameInfoAsync(appId);
            if (gameInfo != null)
            {
                _gameCache[appId] = gameInfo;
                return gameInfo;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting game info for app {appId}");
            return null;
        }
    }

    private async Task<GameInfo?> GetDetailedGameInfoAsync(long appId, string? knownName = null)
    {
        await _apiSemaphore.WaitAsync();
        try
        {
            var storeUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _httpClient.GetAsync(storeUrl);

            if (!response.IsSuccessStatusCode)
            {
                return FallbackGameInfo(appId, knownName);
            }

            var json = await response.Content.ReadAsStringAsync();
            return ParseStoreApiResponse(json, appId, knownName);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // Steam Store API timed out - this is expected for some apps
            _logger.LogWarning($"Steam Store API timeout for app {appId} ({knownName ?? "Unknown"}) - using fallback");
            return FallbackGameInfo(appId, knownName);
        }
        catch (TaskCanceledException)
        {
            // Request was cancelled for other reasons
            _logger.LogWarning($"Steam Store API request cancelled for app {appId} ({knownName ?? "Unknown"}) - using fallback");
            return FallbackGameInfo(appId, knownName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching detailed game info for app {appId}");
            return FallbackGameInfo(appId, knownName);
        }
        finally
        {
            _apiSemaphore.Release();
        }
    }

    private GameInfo? ParseStoreApiResponse(string json, long appId, string? knownName = null)
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
                return FallbackGameInfo(appId, knownName);
            }

            var gameInfo = new GameInfo
            {
                AppId = appId,
                Name = data.TryGetProperty("name", out var name) ? name.GetString() ?? knownName ?? $"App {appId}" : knownName ?? $"App {appId}",
                Type = data.TryGetProperty("type", out var type) ? type.GetString() ?? "game" : "game",
                IsFree = data.TryGetProperty("is_free", out var isFree) && isFree.GetBoolean(),
                HeaderImage = data.TryGetProperty("header_image", out var headerImage) ? headerImage.GetString() : null,
                Description = data.TryGetProperty("short_description", out var desc) ? desc.GetString() : null,
                CacheTime = DateTime.UtcNow
            };

            return gameInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error parsing Store API response for {appId}");
            return FallbackGameInfo(appId, knownName);
        }
    }

    private static GameInfo FallbackGameInfo(long appId, string? knownName = null)
    {
        return new GameInfo
        {
            AppId = appId,
            Name = knownName ?? $"Steam App {appId}",
            Type = "game",
            HeaderImage = null,
            Description = null,
            CacheTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Clear all caches
    /// </summary>
    public void ClearCache()
    {
        _gameCache.Clear();
        _logger.LogInformation("Cleared all Steam service caches");
    }

    #endregion

    public void Dispose()
    {
        _apiSemaphore.Dispose();
    }
}
