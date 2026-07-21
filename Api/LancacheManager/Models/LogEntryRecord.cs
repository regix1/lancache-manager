using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Represents a raw log entry from the access log
/// </summary>
public class LogEntryRecord
{
    [Key]
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    [Required]
    [MaxLength(50)]
    public string ClientIp { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Service { get; set; } = string.Empty;

    [MaxLength(16)]
    public string Method { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Url { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public long BytesServed { get; set; }

    // varchar(10) rejected nginx's REVALIDATED (11 chars) and aborted whole insert
    // batches on bare-metal blizzard logs; 16 covers every nginx cache status.
    [MaxLength(16)]
    public string CacheStatus { get; set; } = string.Empty;

    /// <summary>
    /// Raw HTTP Range request value for newly ingested access-log rows. Historical
    /// rows remain null and are retained by range-specific cleanup.
    /// </summary>
    [MaxLength(200)]
    public string? HttpRange { get; set; }

    public long? DepotId { get; set; }

    /// <summary>
    /// The datasource this log entry belongs to (for multi-datasource support).
    /// Defaults to "default" for backward compatibility.
    /// </summary>
    [MaxLength(100)]
    public string Datasource { get; set; } = "default";

    // Foreign key to associate with download session
    public long? DownloadId { get; set; }
    public virtual Download? Download { get; set; }

    // Index hints for performance
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
