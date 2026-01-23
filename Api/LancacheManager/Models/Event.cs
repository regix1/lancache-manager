using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Models;

public class Event : IUtcMarkable
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // UTC timestamps for storage/queries
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }

    // Local timestamps for display
    public DateTime StartTimeLocal { get; set; }
    public DateTime EndTimeLocal { get; set; }

    // UI customization - index (1-8) referencing theme event colors
    public int ColorIndex { get; set; } = 1;

    // Audit fields
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Navigation property
    public ICollection<EventDownload> EventDownloads { get; set; } = new List<EventDownload>();

    public void MarkDateTimesAsUtc()
    {
        StartTimeUtc = StartTimeUtc.AsUtc();
        EndTimeUtc = EndTimeUtc.AsUtc();
        CreatedAtUtc = CreatedAtUtc.AsUtc();
        UpdatedAtUtc = UpdatedAtUtc.AsUtc();
    }
}
