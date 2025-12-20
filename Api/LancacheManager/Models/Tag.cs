namespace LancacheManager.Models;

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6b7280"; // Default gray
    public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    // Navigation property
    public ICollection<DownloadTag> DownloadTags { get; set; } = new List<DownloadTag>();
}
