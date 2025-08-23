namespace LancacheManager.Models;

public class LogEntry
{
    public DateTime Timestamp { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long BytesServed { get; set; }
    public string CacheStatus { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string? DepotId { get; set; }
}