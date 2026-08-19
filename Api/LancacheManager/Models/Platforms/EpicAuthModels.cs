namespace LancacheManager.Models;

/// <summary>
/// Epic Games authentication data (decrypted in memory)
/// </summary>
public class EpicAuthData
{
    public string? RefreshToken { get; set; }      // Decrypted in memory, encrypted in storage
    public string? DisplayName { get; set; }        // Epic account display name
    public string? AccountId { get; set; }          // Epic account ID
    public DateTime? LastAuthenticated { get; set; }
    public int GamesDiscovered { get; set; }        // Last known count
}

/// <summary>
/// Internal class for JSON serialization with encrypted fields
/// </summary>
public class PersistedEpicAuthData
{
    public string? RefreshToken { get; set; }      // Encrypted with ENC2: prefix
    public string? DisplayName { get; set; }
    public string? AccountId { get; set; }
    public DateTime? LastAuthenticated { get; set; }
    public int GamesDiscovered { get; set; }
}
