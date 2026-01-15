namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// DTO for last prefill result - used for background completion detection
/// </summary>
public class LastPrefillResultDto
{
    public string Status { get; set; } = string.Empty;
    public DateTime CompletedAt { get; set; }
    public int DurationSeconds { get; set; }
}
