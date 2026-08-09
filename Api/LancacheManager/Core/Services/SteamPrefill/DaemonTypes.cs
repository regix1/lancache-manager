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

    /// <summary>
    /// Arguments for the command. Null for the commands that take none, which is most of them.
    /// </summary>
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

    /// <summary>
    /// Human-readable outcome text. Null when the daemon had nothing to say beyond
    /// <see cref="Success"/>.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>
    /// Why the command failed. Null on every successful response, and also null when a failing daemon
    /// reported only a <see cref="Message"/>.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// The command's result body, shaped by the command that was sent. Null for commands that return
    /// nothing and for any command that failed.
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }

    /// <summary>
    /// True when the daemon refused the command because the session has not logged in yet. Null when
    /// the daemon did not send the field at all, which is what older daemon images do.
    /// </summary>
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

    /// <summary>
    /// Detail accompanying <see cref="Status"/>, such as the reason a login failed. Null when the
    /// status stands on its own.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Account name carried on an AuthState update rather than a status reply. Null on every status
    /// that is not an AuthState update, and null while the session is not authenticated.
    /// </summary>
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
    /// and key names (Steam <c>Username</c>, Epic/Xbox <c>accountDisplayName</c>, AuthState
    /// <c>displayName</c>). Candidates are tried in priority order so a payload that includes multiple
    /// aliases still prefers <c>accountDisplayName</c> over <c>username</c> over <c>displayName</c>.
    /// </summary>
    public static string? ParseAccountDisplayName(JsonElement element)
    {
        foreach (var candidate in new[] { "accountDisplayName", "username", "displayName" })
        {
            if (TryGetPropertyCaseInsensitive(element, out var name, candidate)
                && name.ValueKind == JsonValueKind.String)
            {
                var value = name.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the human-readable account name from whichever ingest field was populated
    /// (<see cref="AccountDisplayName"/> from GetStatus, <see cref="DisplayName"/> from AuthState).
    /// </summary>
    public string? ResolveAccountDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(AccountDisplayName))
        {
            return AccountDisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(DisplayName))
        {
            return DisplayName.Trim();
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

    /// <summary>
    /// The account address the challenge was sent to, shown so the user knows where to look for a
    /// code. Null for every credential type that is not delivered by email.
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    /// <summary>
    /// Authorization URL the user opens and pastes the result back from (Epic's flow). Null for
    /// credential types that do not use it.
    /// </summary>
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
    /// Manager-side correlation only: the id of the <see cref="DaemonSession"/> this challenge was
    /// started/resolved on. The daemon never sends this field (it is session-agnostic - see RC3);
    /// the persistent login REST surface stamps it onto every
    /// challenge it returns so the frontend can pin the session a login flow belongs to and reject a
    /// challenge that would otherwise land on a replacement session. Null on any challenge parsed
    /// straight off the wire.
    /// </summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

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

/// <summary>
/// Thrown by <see cref="IDaemonClient.ProvideCredentialAsync"/> when the daemon replies
/// <c>Success=false</c> to a <c>provide-credential</c> command - i.e. it dropped the credential
/// because no matching login challenge was pending for it (RC4).
/// Before the RC4 fix the daemon masked this as <c>Success=true</c>,
/// so a credential misrouted to the wrong session (RC3) was silently celebrated as accepted; the
/// manager now surfaces the drop as a real error instead of a false success.
/// </summary>
public sealed class DaemonCredentialRejectedException : Exception
{
    public DaemonCredentialRejectedException(string message)
        : base(message)
    {
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

    /// <summary>
    /// Why the prefill failed. Null on every successful run.
    /// </summary>
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

    /// <summary>
    /// What the daemon cleared, or why it could not. Null when the daemon returned only
    /// <see cref="Success"/>.
    /// </summary>
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

    /// <summary>
    /// Why this app cannot be prefilled, for example that the account does not own it. Null whenever
    /// the app is available.
    /// </summary>
    [JsonPropertyName("unavailableReason")]
    public string? UnavailableReason { get; set; }
}

public class SelectedAppsStatus
{
    [JsonPropertyName("apps")]
    public List<AppStatus> Apps { get; set; } = new();

    [JsonPropertyName("totalDownloadSize")]
    public long TotalDownloadSize { get; set; }

    /// <summary>
    /// A note about the selection as a whole, such as apps that had to be skipped. Null when the
    /// daemon had nothing to add.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public class CacheStatusResult
{
    [JsonPropertyName("apps")]
    public List<AppCacheStatus> Apps { get; set; } = new();

    /// <summary>
    /// A note about the cache check as a whole, such as apps it could not inspect. Null when every
    /// requested app was checked without incident.
    /// </summary>
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
