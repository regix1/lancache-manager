using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

/// <summary>
/// Lightweight HTTP client that calls Epic Games APIs directly from lancache-manager,
/// without needing a Docker container. Used by EpicMappingService for the
/// Integrations page login flow and periodic catalog refresh.
/// </summary>
public class EpicApiDirectClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EpicApiDirectClient> _logger;

    // Epic Games Launcher OAuth client credentials (public/well-known)
    private const string EpicClientId = "34a02cf8f4414e29b15921876da36f9a";
    private const string EpicClientSecret = "daafbccc737745039dffe53d94fc76cf";

    // API base URLs
    private const string AccountServiceUrl = "https://account-public-service-prod.ol.epicgames.com";
    private const string LauncherServiceUrl = "https://launcher-public-service-prod06.ol.epicgames.com";
    private const string CatalogServiceUrl = "https://catalog-public-service-prod06.ol.epicgames.com";

    public EpicApiDirectClient(ILogger<EpicApiDirectClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Gets the authorization URL for the user to visit in their browser.
    /// After logging in, they receive an authorization code to paste back.
    /// </summary>
    public string GetAuthorizationUrl()
    {
        var redirectUrl = Uri.EscapeDataString(
            $"https://www.epicgames.com/id/api/redirect?clientId={EpicClientId}&responseType=code");
        return $"https://www.epicgames.com/id/login?redirectUrl={redirectUrl}";
    }

    /// <summary>
    /// Exchanges an authorization code for OAuth tokens.
    /// </summary>
    public async Task<EpicOAuthTokens> ExchangeAuthCodeAsync(string authorizationCode, CancellationToken ct = default)
    {
        _logger.LogInformation("Exchanging Epic authorization code for tokens...");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{AccountServiceUrl}/account/api/oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{EpicClientId}:{EpicClientSecret}")));

        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = authorizationCode,
            ["token_type"] = "eg1"
        });

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Epic OAuth token exchange failed: {StatusCode} {Body}", response.StatusCode, json);
            throw new InvalidOperationException($"Epic OAuth failed: {response.StatusCode}. Check your authorization code.");
        }

        var tokenResponse = JsonSerializer.Deserialize<EpicTokenResponse>(json);
        if (tokenResponse == null)
        {
            throw new InvalidOperationException("Failed to parse Epic OAuth response");
        }

        _logger.LogInformation("Epic OAuth successful for {DisplayName} (Account: {AccountId})",
            tokenResponse.DisplayName, tokenResponse.AccountId);

        return new EpicOAuthTokens
        {
            AccessToken = tokenResponse.AccessToken ?? throw new InvalidOperationException("No access token in response"),
            RefreshToken = tokenResponse.RefreshToken ?? throw new InvalidOperationException("No refresh token in response"),
            DisplayName = tokenResponse.DisplayName ?? "Epic User",
            AccountId = tokenResponse.AccountId ?? "",
            ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
            RefreshExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.RefreshExpiresIn)
        };
    }

    /// <summary>
    /// Refreshes tokens using a saved refresh token.
    /// </summary>
    public async Task<EpicOAuthTokens> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        _logger.LogInformation("Refreshing Epic OAuth tokens...");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{AccountServiceUrl}/account/api/oauth/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{EpicClientId}:{EpicClientSecret}")));

        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["token_type"] = "eg1"
        });

        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Epic OAuth refresh failed: {StatusCode} {Body}", response.StatusCode, json);
            throw new InvalidOperationException($"Epic OAuth refresh failed: {response.StatusCode}");
        }

        var tokenResponse = JsonSerializer.Deserialize<EpicTokenResponse>(json);
        if (tokenResponse == null)
        {
            throw new InvalidOperationException("Failed to parse Epic OAuth refresh response");
        }

        _logger.LogInformation("Epic OAuth refresh successful for {DisplayName}", tokenResponse.DisplayName);

        return new EpicOAuthTokens
        {
            AccessToken = tokenResponse.AccessToken ?? throw new InvalidOperationException("No access token"),
            RefreshToken = tokenResponse.RefreshToken ?? throw new InvalidOperationException("No refresh token"),
            DisplayName = tokenResponse.DisplayName ?? "Epic User",
            AccountId = tokenResponse.AccountId ?? "",
            ExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn),
            RefreshExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.RefreshExpiresIn)
        };
    }

    /// <summary>
    /// Fetches owned games from Epic's launcher assets API, then enriches with catalog metadata.
    /// Returns OwnedGame DTOs compatible with EpicGameMappingService.MergeOwnedGamesAsync().
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(string accessToken, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching Epic owned assets...");

        // Step 1: Get owned assets
        var assetsRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{LauncherServiceUrl}/launcher/api/public/assets/Windows?label=Live");
        assetsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var assetsResponse = await _httpClient.SendAsync(assetsRequest, ct);
        var assetsJson = await assetsResponse.Content.ReadAsStringAsync(ct);

        if (!assetsResponse.IsSuccessStatusCode)
        {
            _logger.LogError("Failed to fetch Epic assets: {StatusCode}", assetsResponse.StatusCode);
            throw new InvalidOperationException($"Failed to fetch Epic assets: {assetsResponse.StatusCode}");
        }

        var assets = JsonSerializer.Deserialize<List<EpicAsset>>(assetsJson) ?? new List<EpicAsset>();
        _logger.LogInformation("Found {Count} Epic assets", assets.Count);

        // Step 2: Enrich with catalog metadata (title + images)
        var games = new List<OwnedGame>();
        var batchSize = 25;

        for (var i = 0; i < assets.Count; i += batchSize)
        {
            var batch = assets.Skip(i).Take(batchSize).ToList();

            foreach (var asset in batch)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    var metadata = await GetCatalogMetadataAsync(accessToken, asset.Namespace, asset.CatalogItemId, ct);

                    // Small delay between catalog metadata API calls to avoid rate limiting (429)
                    await Task.Delay(100, ct);

                    _logger.LogTrace("Epic catalog metadata for {AppName}: title={Title}", asset.AppName, metadata?.Title ?? "(null)");
                    if (metadata != null)
                    {
                        var imageUrl = GetBestImageUrl(metadata.KeyImages, asset.AppName);
                        if (imageUrl == null && metadata.KeyImages?.Count > 0)
                        {
                            var types = string.Join(", ", metadata.KeyImages
                                .Where(ki => !string.IsNullOrEmpty(ki.Type))
                                .Select(ki => $"{ki.Type}({ki.Width}x{ki.Height})"));
                            _logger.LogWarning("No landscape image found for {AppName}. Available types: {Types}",
                                asset.AppName, types);
                        }
                        games.Add(new OwnedGame
                        {
                            AppId = asset.AppName,
                            Name = metadata.Title ?? asset.AppName,
                            ImageUrl = imageUrl
                        });
                    }
                    else
                    {
                        games.Add(new OwnedGame
                        {
                            AppId = asset.AppName,
                            Name = asset.AppName
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get catalog metadata for {AppName}, using asset name", asset.AppName);
                    games.Add(new OwnedGame
                    {
                        AppId = asset.AppName,
                        Name = asset.AppName
                    });
                }
            }
        }

        _logger.LogInformation("Resolved {Count} Epic games with metadata", games.Count);
        return games;
    }

    /// <summary>
    /// Gets CDN info from owned assets by looking up each asset's manifest to obtain the
    /// real CDN base URL. The manifest URI contains the actual path used by Epic's CDN
    /// (e.g., /Builds/Org/o-xxx/hash/default) which is what appears in lancache logs.
    /// </summary>
    public async Task<List<CdnInfo>> GetCdnInfoAsync(string accessToken, CancellationToken ct = default)
    {
        _logger.LogInformation("Fetching Epic CDN info from assets...");

        var assetsRequest = new HttpRequestMessage(HttpMethod.Get,
            $"{LauncherServiceUrl}/launcher/api/public/assets/Windows?label=Live");
        assetsRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var assetsResponse = await _httpClient.SendAsync(assetsRequest, ct);
        var assetsJson = await assetsResponse.Content.ReadAsStringAsync(ct);

        if (!assetsResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Failed to fetch Epic assets for CDN info: {StatusCode}", assetsResponse.StatusCode);
            return new List<CdnInfo>();
        }

        var assets = JsonSerializer.Deserialize<List<EpicAsset>>(assetsJson) ?? new List<EpicAsset>();
        _logger.LogInformation("Found {Count} Epic assets, resolving manifest CDN paths...", assets.Count);

        var cdnInfos = new List<CdnInfo>();
        var successCount = 0;
        var failCount = 0;

        foreach (var asset in assets)
        {
            if (ct.IsCancellationRequested) break;

            if (string.IsNullOrEmpty(asset.BuildVersion) || string.IsNullOrEmpty(asset.AppName))
                continue;

            try
            {
                var chunkBaseUrl = await GetManifestBaseUrlAsync(
                    accessToken, asset.Namespace, asset.CatalogItemId, asset.AppName, ct);

                if (!string.IsNullOrEmpty(chunkBaseUrl))
                {
                    cdnInfos.Add(new CdnInfo
                    {
                        AppId = asset.AppName,
                        Name = asset.AppName,
                        CdnHost = "epicgames-download1.akamaized.net",
                        ChunkBaseUrl = chunkBaseUrl
                    });
                    _logger.LogTrace("Resolved Epic CDN path for {AppName}: {Path}", asset.AppName, chunkBaseUrl);
                    successCount++;
                }
                else
                {
                    _logger.LogWarning("Manifest lookup returned no CDN path for {AppName}, skipping", asset.AppName);
                    failCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve manifest CDN path for {AppName}, skipping", asset.AppName);
                failCount++;
            }

            // Small delay between manifest API calls to avoid rate limiting
            await Task.Delay(100, ct);
        }

        _logger.LogInformation(
            "Epic CDN manifest resolution complete: {SuccessCount} succeeded, {FailCount} failed, {TotalCount} total patterns",
            successCount, failCount, cdnInfos.Count);
        return cdnInfos;
    }

    /// <summary>
    /// Calls the Epic manifest API for a single asset to obtain the real CDN base URL.
    /// The manifest URI contains the actual path structure used in lancache logs.
    /// </summary>
    private async Task<string?> GetManifestBaseUrlAsync(
        string accessToken, string ns, string catalogItemId, string appName, CancellationToken ct)
    {
        var url = $"{LauncherServiceUrl}/launcher/api/public/assets/v2/platform/Windows" +
                  $"/namespace/{ns}/catalogItem/{catalogItemId}/app/{appName}/label/Live";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var manifestResponse = JsonSerializer.Deserialize<EpicManifestResponse>(json);

        // Most games have a single Windows element with a single manifest URI.
        // Multiple elements could represent different platform variants, but we only
        // request Windows assets, so the first element/manifest is the correct one.
        var manifestUri = manifestResponse?.Elements?.FirstOrDefault()?.Manifests?.FirstOrDefault()?.Uri;
        if (string.IsNullOrEmpty(manifestUri)) return null;

        // Extract ChunkBaseUrl by stripping the manifest filename from the URI
        // URI: https://epicgames-download1.akamaized.net/Builds/Org/o-xxx/hash/default/manifest.manifest
        // ChunkBaseUrl: /Builds/Org/o-xxx/hash/default
        var uri = new Uri(manifestUri);
        var lastSlash = uri.AbsolutePath.LastIndexOf('/');
        if (lastSlash <= 0) return null; // No meaningful path (e.g., just "/"), skip this entry
        var pathWithoutFile = uri.AbsolutePath[..lastSlash];
        return pathWithoutFile;
    }

    /// <summary>
    /// Fetches catalog metadata for a single item (title + keyImages).
    /// </summary>
    private async Task<EpicCatalogItem?> GetCatalogMetadataAsync(
        string accessToken, string ns, string catalogItemId, CancellationToken ct)
    {
        var url = $"{CatalogServiceUrl}/catalog/api/shared/namespace/{ns}/bulk/items" +
                  $"?id={catalogItemId}&includeDLCDetails=true&includeMainGameDetails=true&country=US&locale=en";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        var items = JsonSerializer.Deserialize<Dictionary<string, EpicCatalogItem>>(json);

        return items?.GetValueOrDefault(catalogItemId);
    }

    /// <summary>
    /// Selects the best landscape image URL from keyImages array.
    /// Only selects wide/landscape images — never portrait/tall.
    /// Appends CDN resize parameters so we fetch a compact 640x360 image.
    /// </summary>
    private static string? GetBestImageUrl(List<EpicKeyImage>? keyImages, string appName = "")
    {
        if (keyImages == null || keyImages.Count == 0) return null;

        // 1. Explicitly-wide types first (guaranteed landscape by name)
        var wideTypes = new[]
        {
            "DieselStoreFrontWide",      // 2560x1440 - primary wide store banner
            "OfferImageWide",            // 2560x1440 - wide offer banner
            "DieselGameBoxWide",         // Wide game box art
        };

        foreach (var wideType in wideTypes)
        {
            var match = keyImages.FirstOrDefault(img =>
                string.Equals(img.Type, wideType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(img.Url));
            if (match != null)
            {
                var result = AppendResizeParams(match.Url!);
                return result;
            }
        }

        // 2. DieselGameBox — very common but can be portrait, so require landscape dimensions
        var dieselGameBox = keyImages.FirstOrDefault(img =>
            string.Equals(img.Type, "DieselGameBox", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(img.Url)
            && img.Width > 0 && img.Height > 0 && img.Width > img.Height);
        if (dieselGameBox != null)
        {
            var result = AppendResizeParams(dieselGameBox.Url!);
            return result;
        }

        // 3. Featured — always landscape (894x488)
        var featured = keyImages.FirstOrDefault(img =>
            string.Equals(img.Type, "Featured", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(img.Url));
        if (featured != null)
        {
            var result = AppendResizeParams(featured.Url!);
            return result;
        }

        // 4. Last resort: pick the widest landscape image by actual dimensions
        var widestLandscape = keyImages
            .Where(img => !string.IsNullOrEmpty(img.Url) && img.Width > 0 && img.Height > 0 && img.Width > img.Height)
            .OrderByDescending(img => (double)img.Width / img.Height)
            .ThenByDescending(img => img.Width)
            .FirstOrDefault();
        if (widestLandscape != null)
        {
            var result = AppendResizeParams(widestLandscape.Url!);
            return result;
        }

        return null;
    }

    /// <summary>
    /// Appends Epic CDN resize parameters to request a compact landscape image.
    /// Uses 640x360 (16:9) which is more than sufficient for card banners and
    /// avoids downloading massive 2560x1440 originals.
    /// </summary>
    private static string AppendResizeParams(string imageUrl)
    {
        var separator = imageUrl.Contains('?') ? "&" : "?";
        return $"{imageUrl}{separator}w=640&h=360&resize=1";
    }

    /// <summary>
    /// Idempotent version of AppendResizeParams: ensures an Epic CDN image URL
    /// includes resize parameters without double-applying them.
    /// Used by GameImagesController to fix legacy DB entries that lack resize params.
    /// </summary>
    internal static string EnsureResizeParams(string imageUrl)
    {
        if (string.IsNullOrEmpty(imageUrl) || !imageUrl.Contains("epicgames.com") || imageUrl.Contains("resize="))
        {
            return imageUrl;
        }

        var separator = imageUrl.Contains('?') ? "&" : "?";
        return $"{imageUrl}{separator}w=640&h=360&resize=1";
    }

    /// <summary>
    /// Fetches currently free games from Epic's public free games promotions API.
    /// This endpoint does NOT require authentication. Returns OwnedGame DTOs
    /// so results can be merged into the mapping DB alongside owned games.
    /// </summary>
    public async Task<List<OwnedGame>> GetFreeGamesAsync(CancellationToken ct = default)
    {
        const string url = "https://store-site-backend-static.ak.epicgames.com/freeGamesPromotions" +
                           "?locale=en-US&country=US&allowCountries=US";

        try
        {
            _logger.LogInformation("Fetching Epic free games promotions...");

            var response = await _httpClient.GetAsync(url, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Epic free games promotions request failed: {StatusCode}", response.StatusCode);
                return new List<OwnedGame>();
            }

            var freeGamesResponse = JsonSerializer.Deserialize<EpicFreeGamesResponse>(json);
            var elements = freeGamesResponse?.Data?.Catalog?.SearchStore?.Elements;

            if (elements == null || elements.Count == 0)
            {
                _logger.LogInformation("No free games found in Epic promotions response");
                return new List<OwnedGame>();
            }

            // Filter to only currently active free promotions (not upcoming ones)
            var activeElements = elements.Where(element =>
                element.Promotions?.PromotionalOffers != null
                && element.Promotions.PromotionalOffers.Count > 0
                && element.Promotions.PromotionalOffers.Any(offer =>
                    offer.PromotionalOffers != null && offer.PromotionalOffers.Count > 0))
                .ToList();

            var games = activeElements.Select(element => new OwnedGame
            {
                AppId = element.Id ?? string.Empty,
                Name = element.Title ?? string.Empty,
                ImageUrl = GetBestImageUrl(element.KeyImages, element.Title ?? element.Id ?? "")
            }).Where(game => !string.IsNullOrEmpty(game.AppId)).ToList();

            _logger.LogInformation("Found {Count} currently free Epic games", games.Count);
            return games;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Epic free games promotions");
            return new List<OwnedGame>();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

#region Epic API Response Models

/// <summary>
/// OAuth token response from Epic's account service.
/// </summary>
public class EpicOAuthTokens
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime RefreshExpiresAt { get; set; }
}

/// <summary>
/// Raw OAuth token response from Epic's API.
/// </summary>
internal class EpicTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAtStr { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("refresh_expires")]
    public int RefreshExpiresIn { get; set; }

    [JsonPropertyName("refresh_expires_at")]
    public string? RefreshExpiresAtStr { get; set; }

    [JsonPropertyName("account_id")]
    public string? AccountId { get; set; }

    [JsonPropertyName("client_id")]
    public string? ClientId { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("app")]
    public string? App { get; set; }

    [JsonPropertyName("in_app_id")]
    public string? InAppId { get; set; }
}

/// <summary>
/// Asset entry from the Epic launcher assets API.
/// </summary>
internal class EpicAsset
{
    [JsonPropertyName("appName")]
    public string AppName { get; set; } = string.Empty;

    [JsonPropertyName("labelName")]
    public string? LabelName { get; set; }

    [JsonPropertyName("buildVersion")]
    public string? BuildVersion { get; set; }

    [JsonPropertyName("catalogItemId")]
    public string CatalogItemId { get; set; } = string.Empty;

    [JsonPropertyName("namespace")]
    public string Namespace { get; set; } = string.Empty;

    [JsonPropertyName("assetId")]
    public string? AssetId { get; set; }
}

/// <summary>
/// Catalog item from Epic's catalog metadata API.
/// </summary>
internal class EpicCatalogItem
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("keyImages")]
    public List<EpicKeyImage>? KeyImages { get; set; }
}

/// <summary>
/// Key image entry from Epic's catalog metadata.
/// </summary>
internal class EpicKeyImage
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

/// <summary>
/// Response from Epic's manifest API (assets v2 endpoint).
/// </summary>
internal class EpicManifestResponse
{
    [JsonPropertyName("elements")]
    public List<EpicManifestElement>? Elements { get; set; }
}

/// <summary>
/// Element entry from the manifest response containing manifest URIs.
/// </summary>
internal class EpicManifestElement
{
    [JsonPropertyName("appName")]
    public string? AppName { get; set; }

    [JsonPropertyName("manifests")]
    public List<EpicManifestEntry>? Manifests { get; set; }
}

/// <summary>
/// Manifest entry containing the URI to the .manifest file on the CDN.
/// </summary>
internal class EpicManifestEntry
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

/// <summary>
/// Top-level response from Epic's free games promotions API.
/// </summary>
internal class EpicFreeGamesResponse
{
    [JsonPropertyName("data")]
    public EpicFreeGamesData? Data { get; set; }
}

/// <summary>
/// Data wrapper in the free games promotions response.
/// </summary>
internal class EpicFreeGamesData
{
    [JsonPropertyName("Catalog")]
    public EpicFreeGamesCatalog? Catalog { get; set; }
}

/// <summary>
/// Catalog wrapper in the free games promotions response.
/// </summary>
internal class EpicFreeGamesCatalog
{
    [JsonPropertyName("searchStore")]
    public EpicFreeGamesSearchStore? SearchStore { get; set; }
}

/// <summary>
/// Search store wrapper containing the elements array.
/// </summary>
internal class EpicFreeGamesSearchStore
{
    [JsonPropertyName("elements")]
    public List<EpicFreeGamesElement>? Elements { get; set; }
}

/// <summary>
/// Individual game element from the free games promotions API.
/// </summary>
internal class EpicFreeGamesElement
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("keyImages")]
    public List<EpicKeyImage>? KeyImages { get; set; }

    [JsonPropertyName("promotions")]
    public EpicFreeGamesPromotions? Promotions { get; set; }
}

/// <summary>
/// Promotions data for a free games element.
/// </summary>
internal class EpicFreeGamesPromotions
{
    [JsonPropertyName("promotionalOffers")]
    public List<EpicPromotionalOfferGroup>? PromotionalOffers { get; set; }

    [JsonPropertyName("upcomingPromotionalOffers")]
    public List<EpicPromotionalOfferGroup>? UpcomingPromotionalOffers { get; set; }
}

/// <summary>
/// Group of promotional offers containing individual offer entries.
/// </summary>
internal class EpicPromotionalOfferGroup
{
    [JsonPropertyName("promotionalOffers")]
    public List<EpicPromotionalOffer>? PromotionalOffers { get; set; }
}

/// <summary>
/// Individual promotional offer with start and end dates.
/// </summary>
internal class EpicPromotionalOffer
{
    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public string? EndDate { get; set; }
}

#endregion
