using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for serving cached game images from the database.
/// Images are fetched and stored by <see cref="GameImageFetchService"/>.
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<ImageCacheService> _logger;

    public ImageCacheService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<ImageCacheService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    public async Task<(byte[] imageBytes, string contentType)?> GetCachedImageAsync(
        string appId,
        string platform,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var image = await db.GameImages
                .AsNoTracking()
                .Where(g => g.AppId == appId && g.Service == platform)
                .FirstOrDefaultAsync(cancellationToken);

            if (image == null || image.ImageData.Length == 0)
                return null;

            return (image.ImageData, image.ContentType);
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
}
