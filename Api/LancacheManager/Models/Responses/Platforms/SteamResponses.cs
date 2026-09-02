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
}

/// <summary>
/// Response for Steam login operations
/// </summary>
public class SteamLoginResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// The current Steam auth mode ("anonymous" | "authenticated"). Null when the response represents
    /// a two-factor, email-code, or session-expired challenge rather than a status report or a
    /// successful login.
    /// </summary>
    public string? AuthMode { get; set; }

    /// <summary>
    /// The authenticated Steam username. Null unless the response reports a successful login or an
    /// already-authenticated session.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Connection status ("connected"). Null except when reporting the current connection status
    /// without an active login attempt.
    /// </summary>
    public string? Status { get; set; }
    public bool RequiresTwoFactor { get; set; }
    public bool RequiresEmailCode { get; set; }
    public bool SessionExpired { get; set; }

    /// <summary>
    /// The tracked operation the sign-in attempt ran under. Null on the status-report responses,
    /// which never started one.
    /// </summary>
    public Guid? OperationId { get; set; }
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
/// Response for API key test. Verdict only: the browser writes the sentence, in the reader's
/// language, from this flag.
/// </summary>
public class ApiKeyTestResponse
{
    public bool Valid { get; set; }
}

/// <summary>
/// Response for API key save
/// </summary>
public class ApiKeySaveResponse
{
    public string Message { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
}


