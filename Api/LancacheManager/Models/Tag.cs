namespace LancacheManager.Models;

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ColorIndex { get; set; } = 1; // Index (1-8) referencing theme event colors
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    // Navigation property
    public ICollection<DownloadTag> DownloadTags { get; set; } = new List<DownloadTag>();
}
