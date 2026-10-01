namespace LancacheManager.Models;

public sealed class EvictionScanCheckpoint
{
    public Guid OperationId { get; set; }
    public int Processed { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }
}
