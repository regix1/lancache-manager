namespace LancacheManager.Models;

public class DownloadTag
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public int DownloadId { get; set; }
    public DateTime TaggedAtUtc { get; set; }

    // Navigation properties
    public Tag Tag { get; set; } = null!;
    public Download Download { get; set; } = null!;
}
