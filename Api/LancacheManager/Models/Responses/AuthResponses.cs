namespace LancacheManager.Models;

/// <summary>
/// Response for authentication status
/// </summary>
public class AuthStatusResponse
{
    public bool RequiresAuth { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? AuthenticationType { get; set; }
    public string? DeviceId { get; set; }
    public string? AuthMode { get; set; }
    public int? GuestTimeRemaining { get; set; }
    public bool HasData { get; set; }
    public bool HasEverBeenSetup { get; set; }
    public bool HasBeenInitialized { get; set; }
    public bool HasDataLoaded { get; set; }
    // Prefill permission for guests
    public bool? PrefillEnabled { get; set; }
    public int? PrefillTimeRemaining { get; set; } // minutes
    // Whether the current device is banned from prefill
    public bool IsBanned { get; set; }
}

/// <summary>
/// Response for API key status
/// </summary>
public class ApiKeyStatusResponse
{
    public bool HasApiKey { get; set; }
    public string KeyType { get; set; } = "none";
    public bool HasPrimaryKey { get; set; }
}

/// <summary>
/// Response for guest session configuration
/// </summary>
public class GuestConfigResponse
{
    public int DurationHours { get; set; }
    public bool IsLocked { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Response for guest duration update
/// </summary>
public class GuestDurationResponse
{
    public bool Success { get; set; }
    public int DurationHours { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for guest mode lock update
/// </summary>
public class GuestLockResponse
{
    public bool Success { get; set; }
    public bool IsLocked { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session clear operation
/// </summary>
public class SessionClearResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session heartbeat (last seen update)
/// </summary>
public class SessionHeartbeatResponse
{
    public bool Success { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Message { get; set; }
}

/// <summary>
/// Response for session creation
/// </summary>
public class SessionCreateResponse
{
    public bool Success { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session deletion
/// </summary>
public class SessionDeleteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session refresh rate setting
/// </summary>
public class SetSessionRefreshRateResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string RefreshRate { get; set; } = string.Empty;
}

/// <summary>
/// Response for device list
/// </summary>
public class DeviceListResponse
{
    public List<object> Devices { get; set; } = new();
    public int Count { get; set; }
}
