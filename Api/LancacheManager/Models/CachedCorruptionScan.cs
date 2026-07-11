using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Header for the one authoritative completed corruption scan. The header is
/// persisted even when the scan has no candidates so an empty result survives
/// process restarts.
/// </summary>
public class CachedCorruptionScan
{
    [Key]
    public Guid ScanId { get; set; }

    public CorruptionDetectionMode DetectionMode { get; set; }

    public int Threshold { get; set; }

    public int ContractVersion { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "completed";

    public DateTime StartedAtUtc { get; set; }

    public DateTime CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CachedCorruptionDetection> Candidates { get; set; } =
        new List<CachedCorruptionDetection>();
}
