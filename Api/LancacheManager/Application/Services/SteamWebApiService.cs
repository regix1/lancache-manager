using System.Text.Json;
using LancacheManager.Infrastructure.Repositories;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service for accessing Steam Web API with automatic V2/V1 fallback
/// - V2: No API key required (api.steampowered.com/v2/)
/// - V1: Requires API key (api.steampowered.com/v1/)
/// </summary>
public class SteamWebApiService
{
    private readonly ILogger<SteamWebApiService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SteamAuthRepository _steamAuthRepository;

    // Cache API version status
    private DateTime _lastStatusCheck = DateTime.MinValue;
    private SteamApiVersion _cachedVersion = SteamApiVersion.Unknown;
    private readonly TimeSpan _statusCheckInterval = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _statusCheckLock = new(1, 1);

    public enum SteamApiVersion
    {
        Unknown,
        V2Active,      // V2 is working, no API key needed
        V1WithKey,     // V2 failed, using V1 with API key
        V1NoKey,       // V2 failed, V1 needs API key but none configured
        BothFailed     // Both V2 and V1 failed
    }

    public class ApiStatus
    {
        public SteamApiVersion Version { get; set; }
        public bool IsV2Available { get; set; }
        public bool IsV1Available { get; set; }
        public bool HasApiKey { get; set; }
        public bool IsFullyOperational { get; set; }
        public string? Message { get; set; }
        public DateTime LastChecked { get; set; }
    }

    public SteamWebApiService(
        ILogger<SteamWebApiService> logger,
        IHttpClientFactory httpClientFactory,
        SteamAuthRepository steamAuthRepository)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _steamAuthRepository = steamAuthRepository;
    }

    /// <summary>
    /// Get current Steam Web API status with V2/V1 fallback information
    /// </summary>
    public async Task<ApiStatus> GetApiStatusAsync(bool forceRefresh = false)
    {
        // Return cached status if recent enough and not forcing refresh
        if (!forceRefresh && DateTime.UtcNow - _lastStatusCheck < _statusCheckInterval && _cachedVersion != SteamApiVersion.Unknown)
        {
            return BuildStatusFromCache();
        }

        // Acquire lock to prevent concurrent status checks
        await _statusCheckLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!forceRefresh && DateTime.UtcNow - _lastStatusCheck < _statusCheckInterval && _cachedVersion != SteamApiVersion.Unknown)
            {
                return BuildStatusFromCache();
            }

            // Check if we have an API key configured
            var authData = _steamAuthRepository.GetSteamAuthData();
            var hasApiKey = !string.IsNullOrWhiteSpace(authData.SteamApiKey);

            // Test V2 first (no API key needed)
            var isV2Available = await TestV2ApiAsync();

            if (isV2Available)
            {
                _cachedVersion = SteamApiVersion.V2Active;
                _lastStatusCheck = DateTime.UtcNow;
                _logger.LogInformation("Steam Web API V2 is available - no API key required");

                return new ApiStatus
                {
                    Version = SteamApiVersion.V2Active,
                    IsV2Available = true,
                    IsV1Available = false, // Not tested since V2 works
                    HasApiKey = hasApiKey,
                    IsFullyOperational = true,
                    Message = "Steam Web API V2 is active (no API key required)",
                    LastChecked = _lastStatusCheck
                };
            }

            _logger.LogWarning("Steam Web API V2 is unavailable - checking V1 fallback");

            // V2 failed, check V1 with API key
            if (hasApiKey)
            {
                var isV1Available = await TestV1ApiAsync(authData.SteamApiKey!);

                if (isV1Available)
                {
                    _cachedVersion = SteamApiVersion.V1WithKey;
                    _lastStatusCheck = DateTime.UtcNow;
                    _logger.LogInformation("Steam Web API V1 is available with configured API key");

                    return new ApiStatus
                    {
                        Version = SteamApiVersion.V1WithKey,
                        IsV2Available = false,
                        IsV1Available = true,
                        HasApiKey = true,
                        IsFullyOperational = true,
                        Message = "Steam Web API V2 unavailable - using V1 with API key",
                        LastChecked = _lastStatusCheck
                    };
                }

                // Both V2 and V1 failed
                _cachedVersion = SteamApiVersion.BothFailed;
                _lastStatusCheck = DateTime.UtcNow;
                _logger.LogError("Both Steam Web API V2 and V1 are unavailable");

                return new ApiStatus
                {
                    Version = SteamApiVersion.BothFailed,
                    IsV2Available = false,
                    IsV1Available = false,
                    HasApiKey = true,
                    IsFullyOperational = false,
                    Message = "Both Steam Web API V2 and V1 are unavailable",
                    LastChecked = _lastStatusCheck
                };
            }

            // V2 failed and no API key configured for V1
            _cachedVersion = SteamApiVersion.V1NoKey;
            _lastStatusCheck = DateTime.UtcNow;
            _logger.LogWarning("Steam Web API V2 unavailable and no V1 API key configured");

            return new ApiStatus
            {
                Version = SteamApiVersion.V1NoKey,
                IsV2Available = false,
                IsV1Available = false, // Can't test without key
                HasApiKey = false,
                IsFullyOperational = false,
                Message = "Steam Web API V2 unavailable - V1 requires API key (not configured)",
                LastChecked = _lastStatusCheck
            };
        }
        finally
        {
            _statusCheckLock.Release();
        }
    }

    /// <summary>
    /// Test Steam Web API V2 availability (no API key required)
    /// Tests the ISteamApps/GetAppList/v2 endpoint which should work without authentication
    /// </summary>
    private async Task<bool> TestV2ApiAsync()
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // V2 endpoint for getting app list (no key required)
            var url = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";

            var response = await client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                // Verify it's actually valid JSON with expected structure
                using var doc = JsonDocument.Parse(content);
                // V2 uses "applist" structure
                if (doc.RootElement.TryGetProperty("applist", out _))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Steam Web API V2 test failed");
            return false;
        }
    }

    /// <summary>
    /// Test Steam Web API V1 availability with provided API key
    /// Uses IStoreService/GetAppList/v1 which validates the API key
    /// </summary>
    private async Task<bool> TestV1ApiAsync(string apiKey)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            // V1 endpoint that validates API key
            var url = $"https://api.steampowered.com/IStoreService/GetAppList/v1/?key={apiKey}&max_results=1";

            var response = await client.GetAsync(url);

            // Check if response is successful (200 OK means valid key)
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                // Verify it's actually valid JSON with expected structure
                using var doc = JsonDocument.Parse(content);
                // Valid response should have "response" property (same as V2 but this one validates the key)
                if (doc.RootElement.TryGetProperty("response", out _))
                {
                    _logger.LogInformation("Steam Web API V1 key validated successfully");
                    return true;
                }
            }

            // Log the status code for debugging (403 = invalid key, 401 = missing key)
            _logger.LogWarning("Steam Web API V1 test failed with status code: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Steam Web API V1 test failed with exception");
            return false;
        }
    }

    /// <summary>
    /// Save or update the Steam Web API key
    /// </summary>
    public void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key cannot be empty", nameof(apiKey));
        }

        _steamAuthRepository.UpdateSteamAuthData(data =>
        {
            data.SteamApiKey = apiKey.Trim();
        });

        // Invalidate cache to force re-check with new key
        _cachedVersion = SteamApiVersion.Unknown;
        _lastStatusCheck = DateTime.MinValue;

        _logger.LogInformation("Steam Web API key saved successfully");
    }

    /// <summary>
    /// Remove the configured Steam Web API key
    /// </summary>
    public void RemoveApiKey()
    {
        _steamAuthRepository.UpdateSteamAuthData(data =>
        {
            data.SteamApiKey = null;
        });

        // Invalidate cache
        _cachedVersion = SteamApiVersion.Unknown;
        _lastStatusCheck = DateTime.MinValue;

        _logger.LogInformation("Steam Web API key removed");
    }

    /// <summary>
    /// Test if a provided API key is valid without saving it
    /// </summary>
    public async Task<bool> TestApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        return await TestV1ApiAsync(apiKey.Trim());
    }

    /// <summary>
    /// Get app list from Steam Web API with automatic V2/V1 fallback
    /// </summary>
    public async Task<List<SteamApp>?> GetAppListAsync()
    {
        var status = await GetApiStatusAsync();

        if (!status.IsFullyOperational)
        {
            _logger.LogWarning("Cannot get app list - Steam Web API is not operational: {Message}", status.Message);
            return null;
        }

        try
        {
            if (status.Version == SteamApiVersion.V2Active)
            {
                // V2 returns all apps in a single request
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);

                var url = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Failed to get app list from V2: HTTP {StatusCode}", response.StatusCode);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                return ParseV2AppList(content);
            }
            else if (status.Version == SteamApiVersion.V1WithKey)
            {
                // V1 requires pagination - fetch all pages
                return await GetV1AppListWithPagination();
            }
            else
            {
                _logger.LogError("Invalid API status for getting app list: {Version}", status.Version);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting app list from Steam Web API");
            return null;
        }
    }

    /// <summary>
    /// Get all apps from V1 API with pagination (V1 limits to 50k per request)
    /// </summary>
    private async Task<List<SteamApp>?> GetV1AppListWithPagination()
    {
        var authData = _steamAuthRepository.GetSteamAuthData();
        var apiKey = authData.SteamApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("Cannot fetch V1 app list - API key is missing");
            return null;
        }

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var allApps = new List<SteamApp>();
        uint lastAppId = 0;
        int pageCount = 0;
        const int maxPages = 20; // Safety limit (50k per page = 1M max apps)

        _logger.LogInformation("Fetching app list from Steam Web API V1 (with pagination)...");

        while (pageCount < maxPages)
        {
            pageCount++;
            var url = $"https://api.steampowered.com/IStoreService/GetAppList/v1/?key={apiKey}&max_results=50000&last_appid={lastAppId}";

            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get V1 app list page {Page}: HTTP {StatusCode}", pageCount, response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            var (apps, hasMore, nextLastAppId) = ParseV1AppListPage(content);

            if (apps == null || apps.Count == 0)
            {
                // No more apps
                break;
            }

            allApps.AddRange(apps);
            _logger.LogInformation("Fetched page {Page}: {Count} apps (total: {Total})", pageCount, apps.Count, allApps.Count);

            if (!hasMore || nextLastAppId == lastAppId)
            {
                // No more pages
                break;
            }

            lastAppId = nextLastAppId;
        }

        _logger.LogInformation("V1 pagination complete: {Total} apps fetched across {Pages} pages", allApps.Count, pageCount);
        return allApps;
    }

    private List<SteamApp>? ParseV2AppList(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // V2 (ISteamApps/GetAppList/v2) uses "applist" → "apps"
            if (!doc.RootElement.TryGetProperty("applist", out var applist) ||
                !applist.TryGetProperty("apps", out var apps))
            {
                return null;
            }

            var appList = new List<SteamApp>();
            foreach (var app in apps.EnumerateArray())
            {
                if (app.TryGetProperty("appid", out var appIdElement) &&
                    app.TryGetProperty("name", out var nameElement))
                {
                    var appId = appIdElement.GetUInt32();
                    var name = nameElement.GetString() ?? $"App {appId}";

                    appList.Add(new SteamApp
                    {
                        AppId = appId,
                        Name = name
                    });
                }
            }

            return appList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse V2 app list response");
            return null;
        }
    }

    /// <summary>
    /// Parse a single page of V1 API response and extract pagination info
    /// </summary>
    /// <returns>Tuple of (apps, hasMore, lastAppId)</returns>
    private (List<SteamApp>? apps, bool hasMore, uint lastAppId) ParseV1AppListPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            // V1 (IStoreService/GetAppList/v1) uses "response" → "apps"
            if (!doc.RootElement.TryGetProperty("response", out var response))
            {
                return (null, false, 0);
            }

            // Check if there are more results
            bool hasMore = false;
            uint lastAppId = 0;

            if (response.TryGetProperty("have_more_results", out var haveMoreElement))
            {
                hasMore = haveMoreElement.GetBoolean();
            }

            if (response.TryGetProperty("last_appid", out var lastAppIdElement))
            {
                lastAppId = lastAppIdElement.GetUInt32();
            }

            // Parse the apps array
            if (!response.TryGetProperty("apps", out var apps))
            {
                return (new List<SteamApp>(), hasMore, lastAppId);
            }

            var appList = new List<SteamApp>();
            foreach (var app in apps.EnumerateArray())
            {
                if (app.TryGetProperty("appid", out var appIdElement) &&
                    app.TryGetProperty("name", out var nameElement))
                {
                    var appId = appIdElement.GetUInt32();
                    var name = nameElement.GetString() ?? $"App {appId}";

                    appList.Add(new SteamApp
                    {
                        AppId = appId,
                        Name = name
                    });
                }
            }

            return (appList, hasMore, lastAppId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse V1 app list page");
            return (null, false, 0);
        }
    }

    private ApiStatus BuildStatusFromCache()
    {
        var authData = _steamAuthRepository.GetSteamAuthData();
        var hasApiKey = !string.IsNullOrWhiteSpace(authData.SteamApiKey);

        return _cachedVersion switch
        {
            SteamApiVersion.V2Active => new ApiStatus
            {
                Version = SteamApiVersion.V2Active,
                IsV2Available = true,
                IsV1Available = false,
                HasApiKey = hasApiKey,
                IsFullyOperational = true,
                Message = "Steam Web API V2 is active (no API key required)",
                LastChecked = _lastStatusCheck
            },
            SteamApiVersion.V1WithKey => new ApiStatus
            {
                Version = SteamApiVersion.V1WithKey,
                IsV2Available = false,
                IsV1Available = true,
                HasApiKey = true,
                IsFullyOperational = true,
                Message = "Steam Web API V2 unavailable - using V1 with API key",
                LastChecked = _lastStatusCheck
            },
            SteamApiVersion.V1NoKey => new ApiStatus
            {
                Version = SteamApiVersion.V1NoKey,
                IsV2Available = false,
                IsV1Available = false,
                HasApiKey = hasApiKey,
                IsFullyOperational = false,
                Message = "Steam Web API V2 unavailable - V1 requires API key (not configured)",
                LastChecked = _lastStatusCheck
            },
            SteamApiVersion.BothFailed => new ApiStatus
            {
                Version = SteamApiVersion.BothFailed,
                IsV2Available = false,
                IsV1Available = false,
                HasApiKey = hasApiKey,
                IsFullyOperational = false,
                Message = "Both Steam Web API V2 and V1 are unavailable",
                LastChecked = _lastStatusCheck
            },
            _ => new ApiStatus
            {
                Version = SteamApiVersion.Unknown,
                IsV2Available = false,
                IsV1Available = false,
                HasApiKey = hasApiKey,
                IsFullyOperational = false,
                Message = "Steam Web API status unknown - check pending",
                LastChecked = _lastStatusCheck
            }
        };
    }

    /// <summary>
    /// Get cached Web API availability status synchronously (doesn't trigger a new check)
    /// Returns true if either V2 is active or V1 is configured with an API key
    /// </summary>
    public bool IsWebApiAvailableCached()
    {
        // Web API is available if V2 works or V1 is configured with a key
        return _cachedVersion == SteamApiVersion.V2Active || _cachedVersion == SteamApiVersion.V1WithKey;
    }

    public class SteamApp
    {
        public uint AppId { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
