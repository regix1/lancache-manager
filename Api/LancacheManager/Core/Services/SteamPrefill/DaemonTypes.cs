using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Serializes a list of string appIds preserving numeric types for daemon compatibility.
/// Steam appIds (numeric) are written as JSON numbers, Epic appIds (hex strings) as JSON strings.
/// </summary>
public static class DaemonSerializer
{
    public static string SerializeAppIds(List<string> appIds)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        foreach (var id in appIds)
        {
            if (long.TryParse(id, out var numericId))
                writer.WriteNumberValue(numericId);
            else
                writer.WriteStringValue(id);
        }
        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

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

/// <summary>
/// Result of a daemon "logout" round-trip, distinguishing a genuine failure from an older daemon
/// image's pre-login command gate rejecting the command outright because the session hasn't
/// finished authenticating yet (its <c>PreLoginCommands</c> allowlist is missing "logout" - see the
/// erase-on-stop regression diagnosis). <see cref="RequiresLogin"/> mirrors
/// <see cref="CommandResponse.RequiresLogin"/> for that rejection case; callers should log/treat it
/// as "nothing to log out yet", not as a real failure.
/// </summary>
public sealed record LogoutOutcome(bool Success, bool RequiresLogin);

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

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The daemon's REAL underlying token expiry (Steam JWT ValidTo / Epic refresh_expires_at /
    /// Xbox refresh-token expiry). Distinct from the manager's 90-day persistent login-validity window.
    /// Null when the daemon does not report it (or reports null). Populated case-insensitively from the
    /// status payload since daemons differ on casing (Steam <c>AuthExpiryUtc</c>, Epic/Xbox <c>authExpiryUtc</c>).
    /// </summary>
    [JsonPropertyName("authExpiryUtc")]
    public DateTimeOffset? AuthExpiryUtc { get; set; }

    /// <summary>
    /// Human-readable account name reported by the daemon (Steam <c>Username</c>,
    /// Epic/Xbox <c>accountDisplayName</c>). Null when not reported.
    /// </summary>
    [JsonPropertyName("accountDisplayName")]
    public string? AccountDisplayName { get; set; }

    /// <summary>
    /// Reads a property from a status payload <see cref="JsonElement"/> case-insensitively.
    /// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> is case-sensitive, but the
    /// account daemons return mixed casing (Steam PascalCase, Epic/Xbox camelCase), so we scan the
    /// object once trying each candidate name with an ordinal-ignore-case comparison.
    /// </summary>
    public static bool TryGetPropertyCaseInsensitive(JsonElement element, out JsonElement value, params string[] candidateNames)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                foreach (var name in candidateNames)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Extracts the daemon's real token expiry from a status payload, tolerant of per-daemon casing
    /// (<c>AuthExpiryUtc</c> vs <c>authExpiryUtc</c>) and a null/absent value. Returns null when not
    /// present, JSON null, or unparseable.
    /// </summary>
    public static DateTimeOffset? ParseAuthExpiry(JsonElement element)
    {
        if (!TryGetPropertyCaseInsensitive(element, out var expiry, "authExpiryUtc"))
        {
            return null;
        }

        return expiry.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => expiry.TryGetDateTimeOffset(out var dto) ? dto : null,
            _ => null
        };
    }

    /// <summary>
    /// Extracts the daemon's account display name from a status payload, tolerant of per-daemon casing
    /// (Steam <c>Username</c>, Epic/Xbox <c>accountDisplayName</c>).
    /// </summary>
    public static string? ParseAccountDisplayName(JsonElement element)
    {
        if (TryGetPropertyCaseInsensitive(element, out var name, "accountDisplayName", "username")
            && name.ValueKind == JsonValueKind.String)
        {
            return name.GetString();
        }

        return null;
    }
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

    [JsonPropertyName("authUrl")]
    public string? AuthUrl { get; set; }

    /// <summary>
    /// Device-code shown to the user for an out-of-band login (Xbox / Microsoft OAuth device-code flow).
    /// The user enters this code at <see cref="VerificationUri"/> in their own browser, so no
    /// password ever enters the daemon container. Null for credential types that do not use device-code.
    /// </summary>
    [JsonPropertyName("userCode")]
    public string? UserCode { get; set; }

    /// <summary>
    /// Verification URL the user opens to enter the <see cref="UserCode"/> (device-code flow).
    /// Distinct from <see cref="AuthUrl"/> (Epic's authorization-URL paste flow). Null when unused.
    /// </summary>
    [JsonPropertyName("verificationUri")]
    public string? VerificationUri { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Parses a credential challenge returned inline in a command response (e.g. get-auto-login-challenge).
    /// </summary>
    public static CredentialChallenge? TryParseFromResponse(
        CommandResponse? response,
        JsonSerializerOptions? options = null)
    {
        if (response?.Success != true || response.Data is not JsonElement element)
        {
            return null;
        }

        try
        {
            var challenge = JsonSerializer.Deserialize<CredentialChallenge>(
                element.GetRawText(),
                options ?? new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return string.IsNullOrWhiteSpace(challenge?.ChallengeId) ? null : challenge;
        }
        catch (JsonException)
        {
            return null;
        }
    }
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
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string AppId { get; set; } = string.Empty;

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

public class CacheStatusResult
{
    [JsonPropertyName("apps")]
    public List<AppCacheStatus> Apps { get; set; } = new();

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class AppCacheStatus
{
    [JsonPropertyName("appId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isUpToDate")]
    public bool IsUpToDate { get; set; }

    [JsonPropertyName("downloadSize")]
    public long DownloadSize { get; set; }

    [JsonPropertyName("outdatedDepots")]
    public List<OutdatedDepot> OutdatedDepots { get; set; } = new();
}

public class OutdatedDepot
{
    [JsonPropertyName("depotId")]
    public long DepotId { get; set; }

    [JsonPropertyName("cachedManifest")]
    public ulong CachedManifest { get; set; }

    [JsonPropertyName("currentManifest")]
    public ulong CurrentManifest { get; set; }
}

/// <summary>
/// Input for cached depot manifest info to send to daemon.
/// Format matches daemon's CachedDepotInput class.
/// </summary>
public class CachedDepotInput
{
    [JsonPropertyName("appId")]
    public long AppId { get; set; }

    [JsonPropertyName("depotId")]
    public long DepotId { get; set; }

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
    public static EncryptedCredentialResponse Encrypt(
        string challengeId,
        string serverPublicKeyBase64,
        string credential,
        string hkdfInfo = "SteamPrefill-Credential-Encryption")
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
            Encoding.UTF8.GetBytes(hkdfInfo)); // Info

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
