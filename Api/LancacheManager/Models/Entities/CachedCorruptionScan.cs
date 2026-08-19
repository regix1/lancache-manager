using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Header for one retained completed corruption scan. The header is persisted
/// even when the scan has no candidates so an empty result survives process
/// restarts. Current identity is explicit and scoped by detection method.
/// </summary>
public class CachedCorruptionScan
{
    [Key]
    public Guid ScanId { get; set; }

    public CorruptionDetectionMode DetectionMode { get; set; }

    /// <summary>
    /// Requested structural scan mode. Null for repeated-MISS scans and for
    /// legacy structural rows whose requested mode was not persisted.
    /// </summary>
    [MaxLength(16)]
    public StructuralScanMode? ScanMode { get; set; }

    /// <summary>Whether this is the actionable current scan for its method.</summary>
    public bool IsCurrent { get; set; }

    public int Threshold { get; set; }

    public int LookbackDays { get; set; }

    public int ContractVersion { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "completed";

    public DateTime StartedAtUtc { get; set; }

    public DateTime CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<CachedCorruptionDetection> Candidates { get; set; } =
        new List<CachedCorruptionDetection>();
}
