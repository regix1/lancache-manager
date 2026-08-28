namespace LancacheManager.Models;

/// <summary>
/// Response for the current game-image cache generation number
/// </summary>
public class GameImageCacheVersionResponse
{
    public long Version { get; set; }
}

/// <summary>
/// Response listing every game image the server can serve, keyed by the id the frontend looks up,
/// with the version each image's bytes were stored at. The frontend puts that version in the banner
/// URL, so one game's new artwork changes one URL instead of all of them.
/// </summary>
public class AvailableGameImagesResponse
{
    public Dictionary<string, long> Images { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Response for clearing the game image cache
/// </summary>
public class ImageCacheClearResponse
{
    public string Message { get; set; } = string.Empty;
    public long CacheGeneration { get; set; }
}
