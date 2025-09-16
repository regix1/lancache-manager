namespace LancacheManager.Models;

public class GameImage
{
    public int Id { get; set; }
    public uint AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string ImageType { get; set; } = "header"; // header, capsule, hero, etc.
    public byte[] ImageData { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/jpeg";
    public DateTime CachedAt { get; set; }
    public DateTime LastAccessed { get; set; }
    public int AccessCount { get; set; }
}