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

    /// <summary>
    /// Reads a stored banner, putting it in the memory cache on the way out.
    /// </summary>
    /// <returns>
    /// Null both when no image is stored for this app and when reading the stored one failed.
    /// The caller falls back to fetching the image again, so a failed read costs a re-fetch
    /// rather than a broken banner.
    /// </returns>
    public async Task<(byte[] imageBytes, string contentType, DateTime storedAtUtc)?> GetImageAsync(
        string appId,
        string platform,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(appId, platform);

        if (_memoryCache.TryGetValue(cacheKey, out (byte[] imageBytes, string contentType, DateTime storedAtUtc) cached))
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

            // UpdatedAtUtc is written only by a refresh that replaced the bytes, so it is the stored-at
            // time whenever one has happened and FetchedAtUtc is it for artwork still on its first copy.
            var result = (image.ImageData, image.ContentType, image.UpdatedAtUtc ?? image.FetchedAtUtc);

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
        EvictMemoryCache();

        // Marked for re-fetch rather than deleted. Deleting emptied the table before the refill
        // started, so /api/game-images/available reported only what had been re-stored so far and
        // every banner past the first batch dropped to a placeholder for the length of the pass.
        // Backdating FetchedAtUtc puts every row under the stale-refresh phase's cutoff instead,
        // and that phase overwrites each row in place, so no banner ever leaves the screen and
        // only art that genuinely changed produces a new version.
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var markedCount = await db.GameImages.ExecuteUpdateAsync(
            setters => setters.SetProperty(g => g.FetchedAtUtc, DateTime.UnixEpoch));
        _logger.LogInformation("[ImageCache] Marked {Count} cached images for re-fetch", markedCount);
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
