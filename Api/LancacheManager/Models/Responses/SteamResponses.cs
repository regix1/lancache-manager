namespace LancacheManager.Models;

/// <summary>
/// Response for Steam authentication status
/// </summary>
public class SteamAuthStatusResponse
{
    public string Mode { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public string AuthMode { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public bool HasStoredCredentials { get; set; }
}

/// <summary>
/// Response for Steam login operations
/// </summary>
public class SteamLoginResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? AuthMode { get; set; }
    public string? Username { get; set; }
    public string? Status { get; set; }
    public bool RequiresTwoFactor { get; set; }
    public bool RequiresEmailCode { get; set; }
    public bool SessionExpired { get; set; }
}

/// <summary>
/// Response for Steam mode update
/// </summary>
public class SteamModeResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
}

/// <summary>
/// Response for Steam API status
/// </summary>
public class SteamApiStatusResponse
{
    public string Version { get; set; } = string.Empty;
    public bool IsV2Available { get; set; }
    public bool IsV1Available { get; set; }
    public bool HasApiKey { get; set; }
    public bool IsFullyOperational { get; set; }
    public string? Message { get; set; }
    public DateTime LastChecked { get; set; }
}

/// <summary>
/// Response for API key test
/// </summary>
public class ApiKeyTestResponse
{
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for API key save
/// </summary>
public class ApiKeySaveResponse
{
    public string Message { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
}

/// <summary>
/// Response for API key removal
/// </summary>
public class ApiKeyRemoveResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for app list
/// </summary>
public class AppListResponse
{
    public int Total { get; set; }
    public int Returned { get; set; }
    public List<object> Apps { get; set; } = new();
}
