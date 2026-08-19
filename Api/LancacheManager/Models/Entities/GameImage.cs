namespace LancacheManager.Models;

public class GameImage
{
    public long Id { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/jpeg";
    public string? SourceUrl { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
