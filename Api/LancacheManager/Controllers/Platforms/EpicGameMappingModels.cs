namespace LancacheManager.Controllers;

/// <summary>
/// Request body for completing Epic auth with authorization code.
/// </summary>
public class EpicAuthCompleteRequest
{
    public string AuthorizationCode { get; set; } = string.Empty;
}

/// <summary>
/// DTO for Epic game mapping responses.
/// </summary>
public class EpicGameMappingDto
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime DiscoveredAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Header image URL for this game. Null until the image fetch service has resolved one.
    /// </summary>
    public string? ImageUrl { get; set; }
}
