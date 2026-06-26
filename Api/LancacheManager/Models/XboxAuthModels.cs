namespace LancacheManager.Models;

/// <summary>
/// Xbox / Microsoft account authentication data (decrypted in memory). Mirrors
/// <see cref="EpicAuthData"/> but carries the MSA refresh token AND the stable device-identity
/// ECDSA private key (so the signed device token keeps validating across restarts). Both the
/// <see cref="RefreshToken"/> and the <see cref="DeviceKeyPkcs8"/> are secrets - encrypted on disk.
/// </summary>
public class XboxAuthData
{
    public string? RefreshToken { get; set; }     // MSA refresh token (decrypted in memory, encrypted in storage)
    public string? DeviceKeyPkcs8 { get; set; }   // device identity ECDSA private key, base64 PKCS#8 (decrypted in memory)
    public string? DisplayName { get; set; }       // Xbox gamertag / display name
    public string? Xuid { get; set; }              // Xbox user id (xuid)
    public DateTime? LastAuthenticated { get; set; }
    public int GamesDiscovered { get; set; }        // Last known count
}

/// <summary>
/// On-disk twin of <see cref="XboxAuthData"/> for JSON serialization: the two secret fields
/// (<see cref="RefreshToken"/> and <see cref="DeviceKeyPkcs8"/>) carry the <c>ENC2:</c> ciphertext.
/// </summary>
public class PersistedXboxAuthData
{
    public string? RefreshToken { get; set; }     // Encrypted with ENC2: prefix
    public string? DeviceKeyPkcs8 { get; set; }   // Encrypted with ENC2: prefix
    public string? DisplayName { get; set; }
    public string? Xuid { get; set; }
    public DateTime? LastAuthenticated { get; set; }
    public int GamesDiscovered { get; set; }
}
