namespace LancacheManager.Models;

/// <summary>
/// Declares what a removal request is asking to delete.
/// </summary>
/// <remarks>
/// Routes that unlink cache files require the caller to send <see cref="CacheFiles"/>. The
/// evicted-items routes delete download and log records only and leave every cached file on
/// disk, so a caller that means <see cref="EvictedRecords"/> cannot reach a cache-file route by
/// sending the wrong request: an undeclared or evicted-records scope is refused there.
/// </remarks>
public static class CacheRemovalScope
{
    /// <summary>Deletes cached files from disk, plus the matching log entries and database rows.</summary>
    public const string CacheFiles = "cacheFiles";

    /// <summary>Deletes evicted download records and their log entries, leaving cached files alone.</summary>
    public const string EvictedRecords = "evictedRecords";
}
