using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public class PrefillCachedApp
{
    public long Id { get; set; }
    public PrefillPlatform Platform { get; set; }
    [Required]
    public string AppId { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? AppName { get; set; }
    public DateTime CachedAtUtc { get; set; }
    [MaxLength(100)]
    public string? CachedBy { get; set; }
    public long TotalBytes { get; set; }
}
