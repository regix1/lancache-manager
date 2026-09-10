using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public class CacheClearProgress
{
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public int DirectoriesProcessed { get; set; }
    public int TotalDirectories { get; set; }
    public long BytesDeleted { get; set; }
    public long FilesDeleted { get; set; }
    public string? Error { get; set; }
    public double PercentComplete { get; set; }
}
