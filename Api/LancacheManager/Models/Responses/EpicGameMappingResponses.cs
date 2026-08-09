namespace LancacheManager.Models;

/// <summary>
/// Response for the Epic mapping login flow start
/// </summary>
public class EpicLoginUrlResponse
{
    public string AuthorizationUrl { get; set; } = string.Empty;
}

/// <summary>
/// Response for completing Epic mapping authentication
/// </summary>
public class EpicAuthCompleteResponse
{
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// The authenticated account's display name. Null when Epic did not return one for this account.
    /// </summary>
    public string? DisplayName { get; set; }
    public int GamesDiscovered { get; set; }
}

/// <summary>
/// Response for cancelling an in-progress Epic catalog refresh
/// </summary>
public class EpicRefreshCancelResponse
{
    public bool Cancelled { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for updating the Epic catalog refresh interval
/// </summary>
public class EpicScheduleIntervalResponse
{
    public double IntervalHours { get; set; }
    public string Message { get; set; } = string.Empty;
}
