using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services.SteamPrefill;

#region DTOs

public class CommandRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, string>? Parameters { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CommandResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("requiresLogin")]
    public bool? RequiresLogin { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTime CompletedAt { get; set; }
}

public class DaemonStatus
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }
}

public class CredentialChallenge
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "credential-challenge";

    [JsonPropertyName("challengeId")]
    public string ChallengeId { get; set; } = string.Empty;

    [JsonPropertyName("credentialType")]
    public string CredentialType { get; set; } = string.Empty;

    [JsonPropertyName("serverPublicKey")]
    public string ServerPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }
}

public class EncryptedCredentialResponse
{
    [JsonPropertyName("challengeId")]
    public string ChallengeId { get; set; } = string.Empty;

    [JsonPropertyName("clientPublicKey")]
    public string ClientPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("encryptedCredential")]
    public string EncryptedCredential { get; set; } = string.Empty;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;
}

public class OwnedGame
{
    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class PrefillResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    /// <summary>
    /// Total time in seconds (for JSON serialization compatibility)
    /// </summary>
    [JsonPropertyName("totalSeconds")]
    public double TotalSeconds => TotalTime.TotalSeconds;
}

public class ClearCacheResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("bytesCleared")]
    public long BytesCleared { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class AppStatus
{
    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("downloadSize")]
    public long DownloadSize { get; set; }

    [JsonPropertyName("isUpToDate")]
    public bool IsUpToDate { get; set; }

    [JsonPropertyName("isUnsupportedOs")]
    public bool IsUnsupportedOs { get; set; }

    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; set; }
}

public class SelectedAppsStatus
{
    [JsonPropertyName("apps")]
    public List<AppStatus> Apps { get; set; } = new();

    [JsonPropertyName("totalDownloadSize")]
    public long TotalDownloadSize { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Input for cached depot manifest info to send to daemon.
/// Format matches daemon's CachedDepotInput class.
/// </summary>
public class CachedDepotInput
{
    [JsonPropertyName("appId")]
    public uint AppId { get; set; }

    [JsonPropertyName("depotId")]
    public uint DepotId { get; set; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }
}

#endregion

#region Crypto

/// <summary>
/// Secure credential exchange using ECDH + AES-GCM
/// The daemon uses raw EC points (uncompressed format: 04 || X || Y)
/// and derives the shared secret using SHA-256 hash of the raw ECDH x-coordinate
/// </summary>
public static class SecureCredentialExchange
{
    /// <summary>
    /// Encrypts credentials using ECDH + HKDF + AES-GCM
    /// Matches the daemon's SecureCredentialExchange implementation exactly
    /// </summary>
    public static EncryptedCredentialResponse EncryptCredentialRaw(
        string challengeId,
        string serverPublicKeyBase64,
        string credential)
    {
        // Parse server public key (65-byte uncompressed EC point)
        var serverPublicKeyBytes = Convert.FromBase64String(serverPublicKeyBase64);
        if (serverPublicKeyBytes.Length != 65 || serverPublicKeyBytes[0] != 0x04)
        {
            throw new CryptographicException($"Invalid server public key format. Expected 65 bytes, got {serverPublicKeyBytes.Length}");
        }

        // Generate client ephemeral keypair
        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var clientParams = clientEcdh.ExportParameters(true);

        // Export client public key as raw EC point (65 bytes: 0x04 + X + Y)
        var clientPublicKey = new byte[65];
        clientPublicKey[0] = 0x04;
        Array.Copy(clientParams.Q.X!, 0, clientPublicKey, 1, 32);
        Array.Copy(clientParams.Q.Y!, 0, clientPublicKey, 33, 32);

        // Import server public key
        using var serverEcdh = ECDiffieHellman.Create();
        var serverParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = serverPublicKeyBytes[1..33],
                Y = serverPublicKeyBytes[33..65]
            }
        };
        serverEcdh.ImportParameters(serverParams);

        // Derive shared secret using .NET's DeriveKeyMaterial
        var sharedSecret = clientEcdh.DeriveKeyMaterial(serverEcdh.PublicKey);

        // Derive AES key using HKDF (matching daemon's implementation)
        var aesKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret,
            32, // 256-bit key
            Encoding.UTF8.GetBytes(challengeId),  // Salt = challengeId
            Encoding.UTF8.GetBytes("SteamPrefill-Credential-Encryption")); // Info

        // Encrypt with AES-GCM
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var plaintextBytes = Encoding.UTF8.GetBytes(credential);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(aesKey, 16);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Securely clear sensitive data
        CryptographicOperations.ZeroMemory(sharedSecret);
        CryptographicOperations.ZeroMemory(aesKey);

        return new EncryptedCredentialResponse
        {
            ChallengeId = challengeId,
            ClientPublicKey = Convert.ToBase64String(clientPublicKey),
            EncryptedCredential = Convert.ToBase64String(ciphertext),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };
    }
}

#endregion
