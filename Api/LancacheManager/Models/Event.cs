namespace LancacheManager.Models;

public class Event
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

    // UI customization - color provided by frontend from theme variables
    public string Color { get; set; } = string.Empty;

    // Audit fields
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    // Navigation property
    public ICollection<EventDownload> EventDownloads { get; set; } = new List<EventDownload>();
}
