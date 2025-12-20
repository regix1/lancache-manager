namespace LancacheManager.Models;

public class EventDownload
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public int DownloadId { get; set; }
    public DateTime TaggedAtUtc { get; set; }
    public bool AutoTagged { get; set; } // True if auto-tagged during event window

    // Navigation properties
    public Event Event { get; set; } = null!;
    public Download Download { get; set; } = null!;
}
