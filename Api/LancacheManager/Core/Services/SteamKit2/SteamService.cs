using System.Collections.Concurrent;
using System.Text.Json;

namespace LancacheManager.Core.Services.SteamKit2;

/// <summary>
/// On-demand Steam game information retrieval (Store API) and depot→app owner lookups.
/// </summary>
public class SteamService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SteamService> _logger;
    private readonly SemaphoreSlim _apiSemaphore = new(5);

    // Caches for performance
    private readonly ConcurrentDictionary<long, GameInfo> _gameCache = new();

    // The store API rate-limits, so an app whose lookup failed is asked again only after an hour.
    private readonly ConcurrentDictionary<long, DateTime> _failedLookupAtUtc = new();
    private static readonly TimeSpan _failedLookupRetryAfter = TimeSpan.FromHours(1);

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
        ILogger<SteamService> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    #region Public API Methods

    /// <summary>
    /// Get game information using real Steam API data
    /// </summary>
    /// <returns>
    /// Null both when Steam knows nothing about the app and when the lookup failed. Callers treat
    /// both as "no name yet" and try again on a later pass.
    /// </returns>
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

                // An expired entry is no longer an answer for this app, so it is dropped here rather
                // than left in place for a later lookup to overwrite.
                _gameCache.TryRemove(appId, out _);
            }

            if (_failedLookupAtUtc.TryGetValue(appId, out var failedAt) &&
                DateTime.UtcNow - failedAt < _failedLookupRetryAfter)
            {
                return null;
            }

            var gameInfo = await GetDetailedGameInfoAsync(appId);
            if (gameInfo != null)
            {
                _gameCache[appId] = gameInfo;
                return gameInfo;
            }

            _failedLookupAtUtc[appId] = DateTime.UtcNow;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting game info for app {appId}");
            return null;
        }
    }

    /// <summary>
    /// App ids whose last store lookup failed less than an hour ago. <see cref="GetGameInfoAsync"/>
    /// answers null for these without asking Steam, so a batch that asks for names can leave them out.
    /// </summary>
    public long[] GetRecentlyFailedAppIds()
    {
        var cutoff = DateTime.UtcNow - _failedLookupRetryAfter;
        return _failedLookupAtUtc
            .Where(failed => failed.Value > cutoff)
            .Select(failed => failed.Key)
            .ToArray();
    }

    /// <summary>
    /// Asks the Steam Store API for an app's details.
    /// </summary>
    /// <returns>
    /// The store's answer, or null when the store could not be reached, answered with an error, or
    /// sent a body without a name.
    /// </returns>
    private async Task<GameInfo?> GetDetailedGameInfoAsync(long appId)
    {
        await _apiSemaphore.WaitAsync();
        try
        {
            var storeUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
            var response = await _httpClient.GetAsync(storeUrl);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            return ParseStoreApiResponse(json, appId);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // Steam Store API timed out - this is expected for some apps
            _logger.LogWarning($"Steam Store API timeout for app {appId}");
            return null;
        }
        catch (TaskCanceledException)
        {
            // Request was cancelled for other reasons
            _logger.LogWarning($"Steam Store API request cancelled for app {appId}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error fetching detailed game info for app {appId}");
            return null;
        }
        finally
        {
            _apiSemaphore.Release();
        }
    }

    /// <summary>
    /// Turns a Steam Store API response body into a game record.
    /// </summary>
    /// <returns>
    /// The parsed store record, or null when the store answered <c>success: false</c>, the body was
    /// not the shape we expected, or it carried no name.
    /// </returns>
    private GameInfo? ParseStoreApiResponse(string json, long appId)
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
                return null;
            }

            var storeName = data.TryGetProperty("name", out var name) ? name.GetString() : null;
            if (string.IsNullOrEmpty(storeName))
            {
                return null;
            }

            var gameInfo = new GameInfo
            {
                AppId = appId,
                Name = storeName,
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
            return null;
        }
    }

    #endregion

    public void Dispose()
    {
        _apiSemaphore.Dispose();
    }
}
