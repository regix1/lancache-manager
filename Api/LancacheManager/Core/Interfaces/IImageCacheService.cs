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
    /// <returns>
    /// Image bytes, content type, and the time those bytes were stored, or null if not cached.
    /// The stored-at time is what makes a banner URL change exactly when its artwork does.
    /// </returns>
    Task<(byte[] imageBytes, string contentType, DateTime storedAtUtc)?> GetImageAsync(
        string appId,
        string platform,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached images from the database.
    /// </summary>
    Task ClearCacheAsync();

    /// <summary>
    /// Evicts all in-memory cached images, forcing the next request to read from the database.
    /// Call this when the cache generation is incremented (e.g., after new images are fetched).
    /// </summary>
    void EvictMemoryCache();
}
