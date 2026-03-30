namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Service for caching game images locally to improve performance
/// </summary>
public interface IImageCacheService
{
    /// <summary>
    /// Gets a cached image from the database by app ID and platform.
    /// Returns null if no image is stored for this app.
    /// </summary>
    /// <param name="appId">The app ID (Steam app ID or Epic app ID)</param>
    /// <param name="platform">The platform ("steam" or "epicgames")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Image bytes and content type, or null if not cached</returns>
    Task<(byte[] imageBytes, string contentType)?> GetCachedImageAsync(
        string appId,
        string platform,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached images
    /// </summary>
    Task ClearCacheAsync();
}
