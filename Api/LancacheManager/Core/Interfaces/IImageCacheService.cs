namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Service for caching game images locally to improve performance
/// </summary>
public interface IImageCacheService
{
    /// <summary>
    /// Gets a cached image if it exists locally and validates the URL hasn't changed
    /// </summary>
    /// <param name="appId">The Steam app ID</param>
    /// <param name="currentImageUrl">Current image URL from database (optional - if null, skips URL validation)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Image bytes and content type, or null if not cached or URL changed</returns>
    Task<(byte[] imageBytes, string contentType)?> GetCachedImageAsync(
        uint appId,
        string? currentImageUrl = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a cached image or downloads it if not cached
    /// </summary>
    /// <param name="appId">The Steam app ID</param>
    /// <param name="imageUrl">The URL to download the image from if not cached</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Image bytes and content type, or null if not available</returns>
    Task<(byte[] imageBytes, string contentType)?> GetOrDownloadImageAsync(
        uint appId,
        string imageUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all cached images
    /// </summary>
    Task ClearCacheAsync();

    /// <summary>
    /// Gets the size of the cache directory in bytes
    /// </summary>
    Task<long> GetCacheSizeAsync();
}
