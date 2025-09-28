using System;
using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Represents a raw log entry from the access log
/// </summary>
public class LogEntryRecord
{
    [Key]
    public int Id { get; set; }

    public DateTime Timestamp { get; set; }

    [Required]
    [MaxLength(50)]
    public string ClientIp { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Service { get; set; } = string.Empty;

    [MaxLength(10)]
    public string Method { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string Url { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public long BytesServed { get; set; }

    [MaxLength(10)]
    public string CacheStatus { get; set; } = string.Empty;

    public uint? DepotId { get; set; }

    // Foreign key to associate with download session
    public int? DownloadId { get; set; }
    public virtual Download? Download { get; set; }

    // Index hints for performance
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}