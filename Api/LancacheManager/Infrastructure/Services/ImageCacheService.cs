using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Service for caching game images locally to disk
/// </summary>
public class ImageCacheService : IImageCacheService
{
    private readonly IPathResolver _pathResolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageCacheService> _logger;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public ImageCacheService(
        IPathResolver pathResolver,
        IHttpClientFactory httpClientFactory,
        ILogger<ImageCacheService> logger)
    {
        _pathResolver = pathResolver;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Ensure cache directory exists
        EnsureCacheDirectoryExists();
    }

    public async Task<(byte[] imageBytes, string contentType)?> GetCachedImageAsync(
        uint appId,
        string? currentImageUrl = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cacheDir = _pathResolver.GetCachedImagesDirectory();
            var cachedFilePath = Path.Combine(cacheDir, $"{appId}.jpg");
            var metadataFilePath = Path.Combine(cacheDir, $"{appId}.jpg.meta");
            var failureMarkerPath = Path.Combine(cacheDir, $"{appId}.failed");

            // Check if this app is known to not have an image (404 marker)
            if (File.Exists(failureMarkerPath))
            {
                _logger.LogTrace($"App {appId} has no image (cached 404)");
                return null;
            }

            // Fast path - check if file exists
            if (File.Exists(cachedFilePath))
            {
                // If currentImageUrl is provided, validate it hasn't changed (PICS update detection)
                if (!string.IsNullOrEmpty(currentImageUrl) && File.Exists(metadataFilePath))
                {
                    var cachedUrl = await File.ReadAllTextAsync(metadataFilePath, cancellationToken);
                    if (cachedUrl.Trim() != currentImageUrl.Trim())
                    {
                        _logger.LogInformation($"Image URL changed for app {appId} (PICS update detected in fast path), cache invalid. Old: {cachedUrl}, New: {currentImageUrl}");
                        // Delete the old cached image so it gets re-downloaded
                        try
                        {
                            File.Delete(cachedFilePath);
                            File.Delete(metadataFilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to delete outdated cache for app {appId}");
                        }
                        return null;
                    }
                }

                var cachedBytes = await File.ReadAllBytesAsync(cachedFilePath, cancellationToken);
                _logger.LogTrace($"Served cached image for app {appId} (fast path)");
                return (cachedBytes, "image/jpeg");
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error reading cached image for app {appId}");
            return null;
        }
    }

    public async Task<(byte[] imageBytes, string contentType)?> GetOrDownloadImageAsync(
        uint appId,
        string imageUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cacheDir = _pathResolver.GetCachedImagesDirectory();
            var cachedFilePath = Path.Combine(cacheDir, $"{appId}.jpg");
            var metadataFilePath = Path.Combine(cacheDir, $"{appId}.jpg.meta");

            // Check if cached file exists
            if (File.Exists(cachedFilePath))
            {
                try
                {
                    // Check if the URL has changed (PICS update detection)
                    bool urlChanged = false;
                    if (File.Exists(metadataFilePath))
                    {
                        var cachedUrl = await File.ReadAllTextAsync(metadataFilePath, cancellationToken);
                        if (cachedUrl.Trim() != imageUrl.Trim())
                        {
                            _logger.LogInformation($"Image URL changed for app {appId} (PICS update detected), will re-download. Old: {cachedUrl}, New: {imageUrl}");
                            urlChanged = true;
                        }
                    }

                    if (!urlChanged)
                    {
                        _logger.LogTrace($"Loading cached image for app {appId} from {cachedFilePath}");
                        var cachedBytes = await File.ReadAllBytesAsync(cachedFilePath, cancellationToken);
                        return (cachedBytes, "image/jpeg");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to read cached image for app {appId}, will re-download");
                    // If reading fails, we'll try to download again
                }
            }

            // Image not cached or read failed, download it
            await _downloadLock.WaitAsync(cancellationToken);
            try
            {
                // Double-check after acquiring lock (another thread might have downloaded it)
                if (File.Exists(cachedFilePath))
                {
                    try
                    {
                        var cachedBytes = await File.ReadAllBytesAsync(cachedFilePath, cancellationToken);
                        return (cachedBytes, "image/jpeg");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to read cached image for app {appId} after lock, will re-download");
                    }
                }

                _logger.LogDebug($"Downloading image for app {appId} from {imageUrl}");

                var httpClient = _httpClientFactory.CreateClient("SteamImages");
                var response = await httpClient.GetAsync(imageUrl, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug($"Failed to download image for app {appId}, status: {response.StatusCode}");

                    // Create a failure marker file to prevent repeated attempts
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        try
                        {
                            var failureMarkerPath = Path.Combine(cacheDir, $"{appId}.failed");
                            await File.WriteAllTextAsync(failureMarkerPath,
                                $"404 - Not Found\nURL: {imageUrl}\nTimestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
                                cancellationToken);
                            _logger.LogDebug($"Created failure marker for app {appId} (no image available)");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to create failure marker for app {appId}");
                        }
                    }

                    return null;
                }

                var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";

                // Save to cache
                try
                {
                    await File.WriteAllBytesAsync(cachedFilePath, imageBytes, cancellationToken);

                    // Save metadata with the image URL for PICS update detection
                    await File.WriteAllTextAsync(metadataFilePath, imageUrl, cancellationToken);

                    _logger.LogDebug($"Cached image for app {appId} to {cachedFilePath}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to cache image for app {appId}");
                    // Continue even if caching fails - we can still return the image
                }

                return (imageBytes, contentType);
            }
            finally
            {
                _downloadLock.Release();
            }
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            _logger.LogWarning($"Timeout downloading image for app {appId}");
            return null;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug($"Download cancelled for app {appId}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error getting or downloading image for app {appId}");
            return null;
        }
    }

    public async Task ClearCacheAsync()
    {
        try
        {
            var cacheDir = _pathResolver.GetCachedImagesDirectory();
            if (Directory.Exists(cacheDir))
            {
                var files = Directory.GetFiles(cacheDir, "*.jpg");
                var metadataFiles = Directory.GetFiles(cacheDir, "*.jpg.meta");
                var failureMarkers = Directory.GetFiles(cacheDir, "*.failed");

                foreach (var file in files)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete cached image: {file}");
                    }
                }

                foreach (var file in metadataFiles)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete metadata file: {file}");
                    }
                }

                foreach (var file in failureMarkers)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete failure marker: {file}");
                    }
                }

                _logger.LogInformation($"Cleared {files.Length} cached images, {metadataFiles.Length} metadata files, and {failureMarkers.Length} failure markers");
            }
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing image cache");
            throw;
        }
    }

    public async Task<long> GetCacheSizeAsync()
    {
        try
        {
            var cacheDir = _pathResolver.GetCachedImagesDirectory();
            if (!Directory.Exists(cacheDir))
            {
                return 0;
            }

            var files = Directory.GetFiles(cacheDir, "*.jpg");
            long totalSize = 0;
            foreach (var file in files)
            {
                try
                {
                    var fileInfo = new FileInfo(file);
                    totalSize += fileInfo.Length;
                }
                catch
                {
                    // Skip files we can't read
                }
            }
            return await Task.FromResult(totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating cache size");
            return 0;
        }
    }

    private void EnsureCacheDirectoryExists()
    {
        try
        {
            var cacheDir = _pathResolver.GetCachedImagesDirectory();
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
                _logger.LogInformation($"Created image cache directory: {cacheDir}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create cache directory");
            throw;
        }
    }
}
