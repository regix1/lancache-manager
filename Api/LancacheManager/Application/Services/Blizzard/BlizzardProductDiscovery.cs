using LancacheManager.Application.Services.Blizzard.Extensions;
using System.Collections.Concurrent;

namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// Discovers and validates Blizzard product codes by querying the CDN.
/// Auto-detects which games are currently available.
/// </summary>
public class BlizzardProductDiscovery
{
    private readonly ILogger<BlizzardProductDiscovery> _logger;
    private readonly LancacheManager.Infrastructure.Services.Interfaces.IImageCacheService _imageCacheService;
    private readonly ConcurrentDictionary<string, BlizzardProductInfo> _discoveredProducts = new();
    private DateTime _lastDiscoveryTime = DateTime.MinValue;
    private readonly TimeSpan _discoveryCacheDuration = TimeSpan.FromHours(24);

    // Known product codes to try (this is a starting list that can be expanded)
    private readonly List<string> _knownProductCodes = new()
    {
        "wow", "wowt", "wow_beta", "wow_classic", "wow_classic_ptr",
        "pro", "prot", "prob",
        "hs", "hsb",
        "s1", "s2", "s2b",
        "hero", "herot",
        "d3", "d3b", "d3t", "d3cn",
        "d4", "d4b",
        "w3", "w3t",
        "viper", "viperb", "vipert", // Call of Duty
        "odinv", "odinb", // Call of Duty Vanguard
        "zeus", // Call of Duty Cold War
        "fenris", "fenrisb", // Diablo 2 Resurrected
        "lazarus", // Modern Warfare
        "anbs", // Diablo Immortal
        "rtro", // Arcade Collection
        "dst2", "dst2a", "dst2b", "dst2e", "dst2i", "dst2t" // Destiny 2
    };

    // Fallback display names if we can't determine from CDN
    private readonly Dictionary<string, string> _fallbackNames = new()
    {
        ["wow"] = "World of Warcraft",
        ["wowt"] = "World of Warcraft (Test)",
        ["wow_beta"] = "World of Warcraft (Beta)",
        ["wow_classic"] = "World of Warcraft Classic",
        ["wow_classic_ptr"] = "World of Warcraft Classic (PTR)",
        ["pro"] = "Overwatch",
        ["prot"] = "Overwatch (Test)",
        ["prob"] = "Overwatch (Beta)",
        ["hs"] = "Hearthstone",
        ["hsb"] = "Hearthstone (Beta)",
        ["s1"] = "StarCraft: Remastered",
        ["s2"] = "StarCraft II",
        ["s2b"] = "StarCraft II (Beta)",
        ["hero"] = "Heroes of the Storm",
        ["herot"] = "Heroes of the Storm (Test)",
        ["d3"] = "Diablo III",
        ["d3b"] = "Diablo III (Beta)",
        ["d3t"] = "Diablo III (Test)",
        ["d3cn"] = "Diablo III (China)",
        ["d4"] = "Diablo IV",
        ["d4b"] = "Diablo IV (Beta)",
        ["w3"] = "Warcraft III: Reforged",
        ["w3t"] = "Warcraft III: Reforged (Test)",
        ["viper"] = "Call of Duty: Warzone",
        ["viperb"] = "Call of Duty: Warzone (Beta)",
        ["vipert"] = "Call of Duty: Warzone (Test)",
        ["odinv"] = "Call of Duty: Vanguard",
        ["odinb"] = "Call of Duty: Vanguard (Beta)",
        ["zeus"] = "Call of Duty: Black Ops Cold War",
        ["fenris"] = "Diablo II: Resurrected",
        ["fenrisb"] = "Diablo II: Resurrected (Beta)",
        ["lazarus"] = "Call of Duty: Modern Warfare",
        ["anbs"] = "Diablo Immortal",
        ["rtro"] = "Blizzard Arcade Collection",
        ["dst2"] = "Destiny 2",
        ["dst2a"] = "Destiny 2 (Alpha)",
        ["dst2b"] = "Destiny 2 (Beta)",
        ["dst2e"] = "Destiny 2 (Event)",
        ["dst2i"] = "Destiny 2 (Internal)",
        ["dst2t"] = "Destiny 2 (Test)"
    };

    // Default game images (can be overridden with real ones later)
    private readonly Dictionary<string, string> _defaultImages = new()
    {
        ["wow"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/P9NWLTJ8CGZZ1509653636320.jpg",
        ["pro"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/XELN72HHVJFK1490208483716.jpg",
        ["hs"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/3AJELOXTWV081541796438560.jpg",
        ["s1"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/OTWQFXEXC7GR1507662223394.jpg",
        ["s2"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/BM48WR3B5CSG1507662259222.jpg",
        ["hero"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/HSS6SR5ZGXJ41507662408363.jpg",
        ["d3"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/GW0BMTBU2DBW1507662338917.jpg",
        ["d4"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/3PCFMPCTQ4UG1656631921663.jpg",
        ["w3"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/PCSH5UBRTF291571862782058.jpg",
        ["fenris"] = "https://bnetcmsus-a.akamaihd.net/cms/gallery/50/50YQ2A8BQFTH1627671898135.jpg"
    };

    public BlizzardProductDiscovery(
        ILogger<BlizzardProductDiscovery> logger,
        LancacheManager.Infrastructure.Services.Interfaces.IImageCacheService imageCacheService)
    {
        _logger = logger;
        _imageCacheService = imageCacheService;
    }

    /// <summary>
    /// Discovers all available Blizzard products by querying the CDN
    /// </summary>
    public async Task<List<BlizzardProductInfo>> DiscoverProductsAsync(bool forceRefresh = false)
    {
        // Return cached results if available and not expired
        if (!forceRefresh &&
            DateTime.UtcNow - _lastDiscoveryTime < _discoveryCacheDuration &&
            _discoveredProducts.Count > 0)
        {
            _logger.LogInformation("Returning cached product discovery results ({Count} products)", _discoveredProducts.Count);
            return _discoveredProducts.Values.OrderBy(p => p.DisplayName).ToList();
        }

        _logger.LogInformation("Discovering Blizzard products...");
        var validProducts = new ConcurrentDictionary<string, BlizzardProductInfo>();
        var checkedCount = 0;
        var foundCount = 0;

        // Check all known product codes in parallel (with rate limiting)
        var semaphore = new SemaphoreSlim(5); // Max 5 concurrent requests
        var tasks = _knownProductCodes.Select(async productCode =>
        {
            await semaphore.WaitAsync();
            try
            {
                var info = await ValidateProductAsync(productCode);
                Interlocked.Increment(ref checkedCount);

                if (info != null)
                {
                    validProducts[productCode] = info;
                    Interlocked.Increment(ref foundCount);
                    _logger.LogInformation("Found active product: {Product} - {Name}", productCode, info.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Product {Product} validation failed: {Error}", productCode, ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation("Product discovery complete: {Found}/{Checked} products active", foundCount, checkedCount);

        // Update cache
        _discoveredProducts.Clear();
        foreach (var kvp in validProducts)
        {
            _discoveredProducts[kvp.Key] = kvp.Value;
        }
        _lastDiscoveryTime = DateTime.UtcNow;

        return validProducts.Values.OrderBy(p => p.DisplayName).ToList();
    }

    /// <summary>
    /// Validates a single product code by querying the CDN
    /// </summary>
    public async Task<BlizzardProductInfo?> ValidateProductAsync(string productCode)
    {
        try
        {
            using var client = new CDNClient(productCode, "us", _logger);

            // Try to get versions - this will fail if product doesn't exist
            var versionsContent = await client.GetVersionsAsync(productCode);

            // Log first few lines for debugging
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                var lines = versionsContent.Split('\n').Take(3);
                _logger.LogTrace("Versions file for {Product}: {Lines}", productCode, string.Join(" | ", lines));
            }

            var version = Parsers.ParseVersions(versionsContent);

            // Try to get build config to extract more info
            string? buildName = null;
            try
            {
                var buildConfigData = await client.DownloadConfigAsync(version.buildConfig.ToMD5());
                var buildConfig = Parsers.ParseBuildConfig(System.Text.Encoding.UTF8.GetString(buildConfigData));
                buildName = buildConfig.buildName;
            }
            catch
            {
                // Build name is optional
            }

            // Get display name (prefer fallback names, they're more readable than build names)
            var displayName = _fallbackNames.GetValueOrDefault(productCode, productCode.ToUpper());

            // Get image URL
            var imageUrl = _defaultImages.GetValueOrDefault(productCode);

            // Download and cache the image if URL is available
            if (!string.IsNullOrEmpty(imageUrl))
            {
                try
                {
                    _ = await _imageCacheService.GetOrDownloadBlizzardImageAsync(productCode, imageUrl);
                    _logger.LogDebug("Downloaded and cached image for product {Product}", productCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download/cache image for product {Product}", productCode);
                    // Continue anyway - image caching failure shouldn't prevent product validation
                }
            }

            return new BlizzardProductInfo
            {
                ProductCode = productCode,
                DisplayName = displayName,
                BuildVersion = buildName,
                ImageUrl = imageUrl,
                BuildConfig = version.buildConfig,
                CDNConfig = version.cdnConfig,
                IsActive = true,
                LastValidated = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Failed to validate product {Product}: {Error}", productCode, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Gets a specific product by code (uses cache if available)
    /// </summary>
    public async Task<BlizzardProductInfo?> GetProductAsync(string productCode)
    {
        // Check cache first
        if (_discoveredProducts.TryGetValue(productCode, out var cached))
        {
            return cached;
        }

        // Try to validate it
        var info = await ValidateProductAsync(productCode);
        if (info != null)
        {
            _discoveredProducts[productCode] = info;
        }

        return info;
    }

    /// <summary>
    /// Gets all discovered products from cache (without re-validating)
    /// </summary>
    public List<BlizzardProductInfo> GetCachedProducts()
    {
        return _discoveredProducts.Values.OrderBy(p => p.DisplayName).ToList();
    }
}

/// <summary>
/// Information about a discovered Blizzard product
/// </summary>
public class BlizzardProductInfo
{
    public string ProductCode { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? BuildVersion { get; set; }
    public string? ImageUrl { get; set; }
    public string BuildConfig { get; set; } = string.Empty;
    public string CDNConfig { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime LastValidated { get; set; }
}
