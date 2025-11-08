using System.Collections.Concurrent;
using LancacheManager.Application.Services.Blizzard;
using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service for managing Blizzard game chunk mappings.
/// Similar to SteamService but for Blizzard's TACT CDN system.
/// </summary>
public class BlizzardService : IHostedService, IDisposable
{
    private readonly ILogger<BlizzardService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Blizzard.BlizzardProductDiscovery _productDiscovery;

    // In-memory cache for fast lookups: (product, archiveIndex, byteOffset) → BlizzardChunkMapping
    private readonly ConcurrentDictionary<(string, int, uint), BlizzardChunkMapping> _chunkCache = new();

    public BlizzardService(
        ILogger<BlizzardService> logger,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        Blizzard.BlizzardProductDiscovery productDiscovery)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _productDiscovery = productDiscovery;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BlizzardService...");

        try
        {
            // Load existing mappings from database
            await LoadExistingMappingsAsync();

            _logger.LogInformation("BlizzardService started with {Count} cached chunks", _chunkCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting BlizzardService");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping BlizzardService...");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Get file info for a Blizzard chunk (archiveIndex + offset)
    /// </summary>
    public BlizzardChunkMapping? GetFileForChunk(string product, int archiveIndex, uint byteOffset)
    {
        // Check cache first
        if (_chunkCache.TryGetValue((product, archiveIndex, byteOffset), out var cached))
        {
            return cached;
        }

        // Query database
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var mapping = context.BlizzardChunkMappings
                .FirstOrDefault(m =>
                    m.Product == product &&
                    m.ArchiveIndex == archiveIndex &&
                    m.ByteOffset == byteOffset);

            if (mapping != null)
            {
                _chunkCache[(product, archiveIndex, byteOffset)] = mapping;
            }

            return mapping;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query chunk mapping for {Product} archive={Index} offset={Offset}",
                product, archiveIndex, byteOffset);
            return null;
        }
    }

    /// <summary>
    /// Build chunk mappings for a specific Blizzard product
    /// </summary>
    public async Task<BuildMappingsResult> BuildMappingsAsync(
        string product,
        string? languageFilter = null,
        string? platformFilter = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Building chunk mappings for {Product}...", product);

        var result = new BuildMappingsResult { Product = product };

        try
        {
            // Create CDN client with logging
            var cdnClient = new CDNClient(product, "us", _logger);
            var mapper = new ChunkMapper(cdnClient, _logger);

            // Build the mapping
            await mapper.BuildMappingAsync(product, languageFilter, platformFilter);

            var mappingCount = mapper.GetMappingCount();
            _logger.LogInformation("Built {Count} chunk mappings for {Product}", mappingCount, product);

            // Save all mappings to database
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get product info from discovery service
            var productInfo = await _productDiscovery.GetProductAsync(product);
            var gameName = productInfo?.DisplayName ?? product.ToUpper();
            var gameImageUrl = productInfo?.ImageUrl;

            int saved = 0;
            int skipped = 0;

            foreach (var file in mapper.GetAllFiles())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("Mapping build cancelled for {Product}", product);
                    result.Success = false;
                    result.Error = "Operation cancelled";
                    return result;
                }

                var existing = await context.BlizzardChunkMappings
                    .FirstOrDefaultAsync(m =>
                        m.Product == product &&
                        m.ArchiveIndex == file.Location.ArchiveIndex &&
                        m.ByteOffset == file.Location.ByteOffset, cancellationToken);

                if (existing == null)
                {
                    var mapping = new BlizzardChunkMapping
                    {
                        Product = product,
                        ArchiveIndex = file.Location.ArchiveIndex,
                        ByteOffset = file.Location.ByteOffset,
                        FileName = file.FileName,
                        FileSize = file.Size,
                        ContentHash = file.ContentHash.ToHexString(),
                        GameName = gameName,
                        GameImageUrl = gameImageUrl,
                        Source = "chunk-mapper"
                    };

                    context.BlizzardChunkMappings.Add(mapping);
                    _chunkCache[(product, file.Location.ArchiveIndex, file.Location.ByteOffset)] = mapping;
                    saved++;
                }
                else
                {
                    skipped++;
                }

                // Batch save every 1000 entries to avoid memory issues
                if ((saved + skipped) % 1000 == 0)
                {
                    await context.SaveChangesAsync(cancellationToken);
                    _logger.LogInformation("Progress: {Saved} saved, {Skipped} skipped", saved, skipped);
                }
            }

            // Final save
            await context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Saved {Saved} new chunk mappings for {Product} ({Skipped} already existed)",
                saved, product, skipped);

            result.Success = true;
            result.MappingsCreated = saved;
            result.MappingsSkipped = skipped;
            result.TotalMappings = saved + skipped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building chunk mappings for {Product}", product);
            result.Success = false;
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Get statistics about stored mappings
    /// </summary>
    public async Task<BlizzardMappingStats> GetStatsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stats = new BlizzardMappingStats();

        try
        {
            stats.TotalMappings = await context.BlizzardChunkMappings.CountAsync();

            var productStats = await context.BlizzardChunkMappings
                .GroupBy(m => m.Product)
                .Select(g => new ProductMappingStats
                {
                    Product = g.Key,
                    Count = g.Count(),
                    GameName = g.First().GameName ?? g.Key.ToUpper()
                })
                .ToListAsync();

            stats.ProductStats = productStats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting Blizzard mapping stats");
        }

        return stats;
    }

    /// <summary>
    /// Clear all mappings for a specific product
    /// </summary>
    public async Task<int> ClearProductMappingsAsync(string product)
    {
        _logger.LogInformation("Clearing chunk mappings for {Product}...", product);

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mappings = context.BlizzardChunkMappings.Where(m => m.Product == product);
        var count = await mappings.CountAsync();

        context.BlizzardChunkMappings.RemoveRange(mappings);
        await context.SaveChangesAsync();

        // Clear from cache
        var keysToRemove = _chunkCache.Keys.Where(k => k.Item1 == product).ToList();
        foreach (var key in keysToRemove)
        {
            _chunkCache.TryRemove(key, out _);
        }

        _logger.LogInformation("Cleared {Count} chunk mappings for {Product}", count, product);

        return count;
    }

    /// <summary>
    /// Discover available Blizzard products
    /// </summary>
    public async Task<List<Blizzard.BlizzardProductInfo>> DiscoverProductsAsync(bool forceRefresh = false)
    {
        return await _productDiscovery.DiscoverProductsAsync(forceRefresh);
    }

    /// <summary>
    /// Get cached list of discovered products
    /// </summary>
    public List<Blizzard.BlizzardProductInfo> GetDiscoveredProducts()
    {
        return _productDiscovery.GetCachedProducts();
    }

    /// <summary>
    /// Validate a specific product code
    /// </summary>
    public async Task<Blizzard.BlizzardProductInfo?> ValidateProductAsync(string productCode)
    {
        return await _productDiscovery.ValidateProductAsync(productCode);
    }

    /// <summary>
    /// Load existing mappings from database into cache
    /// </summary>
    private async Task LoadExistingMappingsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Only load a sample for initial cache (loading all could use too much memory)
        var recentMappings = await context.BlizzardChunkMappings
            .OrderByDescending(m => m.DiscoveredAt)
            .Take(10000)
            .ToListAsync();

        foreach (var mapping in recentMappings)
        {
            _chunkCache[(mapping.Product, mapping.ArchiveIndex, mapping.ByteOffset)] = mapping;
        }

        _logger.LogInformation("Loaded {Count} recent Blizzard chunk mappings into cache", recentMappings.Count);
    }

    public void Dispose()
    {
        // Cleanup if needed
    }
}

/// <summary>
/// Result of building chunk mappings
/// </summary>
public class BuildMappingsResult
{
    public bool Success { get; set; }
    public string Product { get; set; } = string.Empty;
    public int MappingsCreated { get; set; }
    public int MappingsSkipped { get; set; }
    public int TotalMappings { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Statistics about Blizzard chunk mappings
/// </summary>
public class BlizzardMappingStats
{
    public int TotalMappings { get; set; }
    public List<ProductMappingStats> ProductStats { get; set; } = new();
}

public class ProductMappingStats
{
    public string Product { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public int Count { get; set; }
}
