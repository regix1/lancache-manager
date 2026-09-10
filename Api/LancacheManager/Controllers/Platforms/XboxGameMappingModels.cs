namespace LancacheManager.Controllers;

/// <summary>
/// DTO for Xbox game mapping responses.
/// </summary>
public class XboxGameMappingDto
{
    public string ProductId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime DiscoveredAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public string? ImageUrl { get; set; }
}

/// <summary>
/// DTO for Xbox mapping statistics.
/// </summary>
public class XboxMappingStatsDto
{
    public int TotalGames { get; set; }
    public DateTime? LastUpdatedUtc { get; set; }
}
