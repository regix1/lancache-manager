using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Singleton row storing deduplicated on-disk cache totals computed after each detection scan.
/// Dashboard reads these values instead of re-statting files on every request.
/// </summary>
public class CachedDetectionSummary
{
    public const int SingletonId = 1;

    [Key]
    public int Id { get; set; } = SingletonId;

    public ulong GamesOnDiskBytes { get; set; }

    public int GamesOnDiskCount { get; set; }

    public ulong IdentifiedCacheBytes { get; set; }

    public ulong IdentifiedServiceBytes { get; set; }

    public int IdentifiedServiceCount { get; set; }

    public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
}
