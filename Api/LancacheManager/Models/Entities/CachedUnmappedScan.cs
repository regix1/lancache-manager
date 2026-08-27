using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Header for the retained unmapped-cache scan. The header is persisted even when the
/// scan found no unmapped files so an empty result survives process restarts. Only the
/// most recent scan is kept; a new scan replaces it.
/// </summary>
public class CachedUnmappedScan
{
    [Key]
    public Guid ScanId { get; set; }

    public int ContractVersion { get; set; }

    public DateTime CompletedAtUtc { get; set; }
}
