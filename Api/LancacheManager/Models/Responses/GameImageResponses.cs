namespace LancacheManager.Models;

/// <summary>
/// Response for the current game-image cache generation number
/// </summary>
public class GameImageCacheVersionResponse
{
    public long Version { get; set; }
}

/// <summary>
/// Response for clearing the game image cache
/// </summary>
public class ImageCacheClearResponse
{
    public string Message { get; set; } = string.Empty;
    public int EpicImageUrlsRefreshed { get; set; }
    public long CacheGeneration { get; set; }
}
