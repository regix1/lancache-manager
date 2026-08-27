using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// One service's unmapped cache files from a completed scan, stored as a versioned JSON
/// array in the canonical snake_case contract the scanner emits. One row covers a whole
/// service within one datasource, so the individual files live inside <see cref="FilesJson"/>
/// rather than one per row.
/// </summary>
public class CachedUnmappedDetection
{
    [Key]
    public long Id { get; set; }

    public Guid ScanId { get; set; }

    public CachedUnmappedScan? Scan { get; set; }

    /// <summary>Service the recovered upstream URL belongs to, or "unknown".</summary>
    [MaxLength(100)]
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>The datasource whose cache root owns these paths.</summary>
    [MaxLength(100)]
    public string DatasourceName { get; set; } = "default";

    /// <summary>
    /// Totals for this group, stored rather than derived so the section summary reads them
    /// without pulling <see cref="FilesJson"/>, which runs to tens of thousands of entries.
    /// </summary>
    public long FileCount { get; set; }

    public long TotalSizeBytes { get; set; }

    public string FilesJson { get; set; } = "[]";
}
