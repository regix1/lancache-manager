namespace LancacheManager.Models;

/// <summary>
/// Steam authentication data (decrypted in memory)
/// NOTE: GuardData is NOT stored - modern Steam auth uses refresh tokens only
/// </summary>
public class SteamAuthData
{
    public string Mode { get; set; } = "anonymous"; // "anonymous" or "authenticated"
    public string? Username { get; set; }
    public string? RefreshToken { get; set; } // Decrypted in memory, encrypted in storage
    public DateTime? LastAuthenticated { get; set; }
    public string? SteamApiKey { get; set; } // Steam Web API key for V1 fallback (decrypted in memory, encrypted in storage)
}

/// <summary>
/// Internal class for JSON serialization with encrypted fields
/// NOTE: GuardData is NOT stored - modern Steam auth uses refresh tokens only
/// </summary>
public class PersistedSteamAuthData
{
    public string Mode { get; set; } = "anonymous";
    public string? Username { get; set; }
    public string? RefreshToken { get; set; } // Encrypted with ENC2: prefix
    public DateTime? LastAuthenticated { get; set; }
    public string? SteamApiKey { get; set; } // Encrypted with ENC2: prefix
}
