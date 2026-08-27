namespace LancacheManager.Models;

/// <summary>Status of the background count of the cache files a service removal would delete.</summary>
/// <remarks>
/// The removal confirmation polls this. It shows the progress line while the count runs and the
/// counted number once it lands, so the number a user confirms against describes the files the
/// removal will actually delete rather than a detection scan's older snapshot.
/// </remarks>
public sealed class CacheFileCountStatusResponse
{
    public bool IsProcessing { get; set; }

    /// <summary>
    /// The count this body describes. The caller compares it against the id it started, so a
    /// finished count belonging to an earlier run can never be read as this one's answer.
    /// </summary>
    public Guid? OperationId { get; set; }

    public double PercentComplete { get; set; }

    public string? StageKey { get; set; }

    /// <summary>
    /// Interpolation values for the stage key. The tracker stores only the key string, so a
    /// placeholder-bearing key like signalr.serviceRemove.counting.progress would otherwise render
    /// its raw {{n}} and {{total}}.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Context { get; set; }

    /// <summary>
    /// Files found on disk. Present only once the count finished, because a partial walk would be
    /// a number smaller than what the removal reaches.
    /// </summary>
    public int? CacheFilesFound { get; set; }
}
