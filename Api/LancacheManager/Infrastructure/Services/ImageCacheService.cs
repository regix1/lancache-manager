using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for serving cached game images from the database.
/// Uses an in-memory cache to avoid repeated DB reads for the same image.
/// Images are fetched and stored by <see cref="GameImageFetchService"/>.
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly IMemoryCache _memoryCache;

    /// <summary>
    /// CancellationTokenSource used to bulk-evict all cached image entries.
    /// When cancelled, all cache entries linked to it are automatically removed.
    /// </summary>
    private CancellationTokenSource _evictionTokenSource = new();

    private static readonly TimeSpan _slidingExpiration = TimeSpan.FromHours(24);

    public ImageCacheService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<ImageCacheService> logger,
        IMemoryCache memoryCache)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _memoryCache = memoryCache;
    }

    public async Task<(byte[] imageBytes, string contentType)?> GetCachedImageAsync(
        string appId,
        string platform,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(appId, platform);

        if (_memoryCache.TryGetValue(cacheKey, out (byte[] imageBytes, string contentType) cached))
        {
            return cached;
        }

        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var image = await db.GameImages
                .AsNoTracking()
                .Where(g => g.AppId == appId && g.Service == platform)
                .FirstOrDefaultAsync(cancellationToken);

            if (image == null || image.ImageData.Length == 0)
                return null;

            var result = (image.ImageData, image.ContentType);

            var entryOptions = new MemoryCacheEntryOptions()
                .SetSlidingExpiration(_slidingExpiration)
                .SetSize(image.ImageData.Length)
                .AddExpirationToken(new CancellationChangeToken(_evictionTokenSource.Token));

            _memoryCache.Set(cacheKey, result, entryOptions);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ImageCache] Error reading cached image for {AppId} ({Platform})", appId, platform);
            return null;
        }
    }

    public async Task ClearCacheAsync()
    {
        try
        {
            EvictMemoryCache();

            await using var db = await _dbContextFactory.CreateDbContextAsync();
            var deletedCount = await db.GameImages.ExecuteDeleteAsync();
            _logger.LogInformation("[ImageCache] Cleared {Count} cached images from the database", deletedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ImageCache] Error clearing cached images from the database");
            throw;
        }
    }

    /// <inheritdoc />
    public void EvictMemoryCache()
    {
        var oldTokenSource = _evictionTokenSource;
        _evictionTokenSource = new CancellationTokenSource();

        // Cancelling the old token evicts every cache entry that was linked to it
        oldTokenSource.Cancel();
        oldTokenSource.Dispose();

        _logger.LogInformation("[ImageCache] In-memory image cache evicted");
    }

    private static string BuildCacheKey(string appId, string platform) =>
        $"game-image:{appId}:{platform}";
}
