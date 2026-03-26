namespace LancacheManager.Models;

public class EventDownload
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long DownloadId { get; set; }
    public DateTime TaggedAtUtc { get; set; }
    public bool AutoTagged { get; set; } // True if auto-tagged during event window

    // Navigation properties
    public Event Event { get; set; } = null!;
    public Download Download { get; set; } = null!;
}
