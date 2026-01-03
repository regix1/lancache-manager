using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Application.SteamPrefill;

/// <summary>
/// Client library for communicating with SteamPrefill daemon.
/// Uses secure encrypted credential exchange (ECDH + AES-GCM).
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private readonly string _commandsDir;
    private readonly string _responsesDir;
    private readonly FileSystemWatcher? _responseWatcher;
    private readonly Dictionary<string, TaskCompletionSource<CommandResponse>> _pendingCommands = new();
    private readonly Dictionary<string, TaskCompletionSource<CredentialChallenge>> _pendingChallenges = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public DaemonClient(string commandsDir, string responsesDir)
    {
        _commandsDir = commandsDir;
        _responsesDir = responsesDir;

        Directory.CreateDirectory(_commandsDir);
        Directory.CreateDirectory(_responsesDir);

        // Watch for responses
        _responseWatcher = new FileSystemWatcher(_responsesDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        _responseWatcher.Created += OnResponseFileCreated;
    }

    private async void OnResponseFileCreated(object sender, FileSystemEventArgs e)
    {
        try
        {
            await Task.Delay(50); // Ensure file is written

            var json = await File.ReadAllTextAsync(e.FullPath);

            // Check if it's a credential challenge
            if (json.Contains("\"type\":\"credential-challenge\"", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("\"type\": \"credential-challenge\"", StringComparison.OrdinalIgnoreCase))
            {
                var challenge = JsonSerializer.Deserialize<CredentialChallenge>(json);
                if (challenge != null)
                {
                    lock (_pendingChallenges)
                    {
                        if (_pendingChallenges.TryGetValue("pending", out var tcs))
                        {
                            tcs.TrySetResult(challenge);
                        }
                    }
                }
                return;
            }

            // Check if it's a command response
            if (json.Contains("\"id\"", StringComparison.OrdinalIgnoreCase))
            {
                var response = JsonSerializer.Deserialize<CommandResponse>(json);
                if (response != null)
                {
                    lock (_pendingCommands)
                    {
                        if (_pendingCommands.TryGetValue(response.Id, out var tcs))
                        {
                            tcs.TrySetResult(response);
                            _pendingCommands.Remove(response.Id);
                        }
                    }

                    // Clean up response file
                    try { File.Delete(e.FullPath); } catch { }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }
    }

    /// <summary>
    /// Get the current daemon status
    /// </summary>
    public async Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var statusPath = Path.Combine(_responsesDir, "daemon_status.json");
        if (!File.Exists(statusPath))
            return null;

        var json = await File.ReadAllTextAsync(statusPath, cancellationToken);
        return JsonSerializer.Deserialize<DaemonStatus>(json);
    }

    /// <summary>
    /// Send a command to the daemon and wait for response
    /// </summary>
    public async Task<CommandResponse> SendCommandAsync(
        string type,
        Dictionary<string, string>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var command = new CommandRequest
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Parameters = parameters,
            CreatedAt = DateTime.UtcNow
        };

        var tcs = new TaskCompletionSource<CommandResponse>();

        lock (_pendingCommands)
        {
            _pendingCommands[command.Id] = tcs;
        }

        try
        {
            // Write command file
            var json = JsonSerializer.Serialize(command);
            var filePath = Path.Combine(_commandsDir, $"cmd_{command.Id}.json");
            await File.WriteAllTextAsync(filePath, json, cancellationToken);

            // Wait for response
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        finally
        {
            lock (_pendingCommands)
            {
                _pendingCommands.Remove(command.Id);
            }
        }
    }

    /// <summary>
    /// Check for an existing credential challenge
    /// </summary>
    public async Task<CredentialChallenge?> GetPendingChallengeAsync(CancellationToken cancellationToken = default)
    {
        var challengeFiles = Directory.GetFiles(_responsesDir, "auth_challenge_*.json");
        foreach (var file in challengeFiles)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var challenge = JsonSerializer.Deserialize<CredentialChallenge>(json);
                if (challenge != null && DateTime.UtcNow < challenge.ExpiresAt)
                {
                    return challenge;
                }
                if (challenge != null && DateTime.UtcNow >= challenge.ExpiresAt)
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Start login process
    /// </summary>
    public async Task<CredentialChallenge?> StartLoginAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var existingChallenge = await GetPendingChallengeAsync(cancellationToken);
        if (existingChallenge != null)
        {
            return existingChallenge;
        }

        // Send login command (don't wait for response)
        _ = SendCommandAsync("login", cancellationToken: cancellationToken);

        // Poll for credential challenge (FileSystemWatcher may not work on Docker bind mounts)
        var timeoutTime = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < timeoutTime && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(200, cancellationToken);

            var challenge = await GetPendingChallengeAsync(cancellationToken);
            if (challenge != null)
            {
                return challenge;
            }
        }

        return null;
    }

    /// <summary>
    /// Provide encrypted credential
    /// </summary>
    public async Task ProvideCredentialAsync(
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        // Use raw ECDH method for better interoperability with the daemon
        var encrypted = SecureCredentialExchange.EncryptCredentialRaw(
            challenge.ChallengeId,
            challenge.ServerPublicKey,
            credential);

        var json = JsonSerializer.Serialize(encrypted, new JsonSerializerOptions { WriteIndented = true });
        // Use cmd_ prefix like other commands - daemon watches for cmd_*.json
        var filePath = Path.Combine(_commandsDir, $"cmd_{encrypted.ChallengeId}.json");

        // Debug: log what we're writing
        Console.WriteLine($"[DEBUG] Writing credential file: {filePath}");
        Console.WriteLine($"[DEBUG] Challenge ID: {challenge.ChallengeId}");
        Console.WriteLine($"[DEBUG] Server public key length: {Convert.FromBase64String(challenge.ServerPublicKey).Length}");
        Console.WriteLine($"[DEBUG] Client public key: {encrypted.ClientPublicKey[..20]}...");
        Console.WriteLine($"[DEBUG] Encrypted credential length: {Convert.FromBase64String(encrypted.EncryptedCredential).Length}");
        Console.WriteLine($"[DEBUG] JSON content:\n{json}");

        await File.WriteAllTextAsync(filePath, json, cancellationToken);

        // Delete challenge file
        var challengeFile = Path.Combine(_responsesDir, $"auth_challenge_{challenge.ChallengeId}.json");
        try { File.Delete(challengeFile); } catch { }
    }

    /// <summary>
    /// Wait for next credential challenge
    /// </summary>
    public async Task<CredentialChallenge?> WaitForChallengeAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // Poll for credential challenge (FileSystemWatcher may not work on Docker bind mounts)
        var timeoutTime = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromMinutes(5));
        while (DateTime.UtcNow < timeoutTime && !cancellationToken.IsCancellationRequested)
        {
            var challenge = await GetPendingChallengeAsync(cancellationToken);
            if (challenge != null)
            {
                return challenge;
            }

            await Task.Delay(200, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Get owned games
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-owned-games", cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to get owned games");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<List<OwnedGame>>(element.GetRawText()) ?? new List<OwnedGame>();
        }

        return new List<OwnedGame>();
    }

    /// <summary>
    /// Set selected apps for prefill
    /// </summary>
    public async Task SetSelectedAppsAsync(List<uint> appIds, CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("set-selected-apps", new Dictionary<string, string>
        {
            ["appIds"] = JsonSerializer.Serialize(appIds)
        }, cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to set selected apps");
    }

    /// <summary>
    /// Start prefill
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        bool all = false,
        bool recent = false,
        bool recentlyPurchased = false,
        int? top = null,
        bool force = false,
        List<string>? operatingSystems = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>();
        if (all) parameters["all"] = "true";
        if (recent) parameters["recent"] = "true";
        if (recentlyPurchased) parameters["recentlyPurchased"] = "true";
        if (top.HasValue) parameters["top"] = top.Value.ToString();
        if (force) parameters["force"] = "true";
        if (operatingSystems != null && operatingSystems.Count > 0)
            parameters["os"] = string.Join(",", operatingSystems);

        var response = await SendCommandAsync("prefill", parameters,
            timeout: TimeSpan.FromHours(24),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Prefill failed");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<PrefillResult>(element.GetRawText())
                   ?? new PrefillResult { Success = false, ErrorMessage = "Failed to parse result" };
        }

        return new PrefillResult { Success = true };
    }

    /// <summary>
    /// Clear the temporary cache
    /// </summary>
    public async Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("clear-cache", cancellationToken: cancellationToken);

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<ClearCacheResult>(element.GetRawText())
                   ?? new ClearCacheResult { Success = false, Message = "Failed to parse result" };
        }

        return new ClearCacheResult { Success = response.Success, Message = response.Message };
    }

    /// <summary>
    /// Get cache info
    /// </summary>
    public async Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-cache-info", cancellationToken: cancellationToken);

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<ClearCacheResult>(element.GetRawText())
                   ?? new ClearCacheResult { Success = false, Message = "Failed to parse result" };
        }

        return new ClearCacheResult { Success = response.Success, Message = response.Message };
    }

    /// <summary>
    /// Get selected apps status with download sizes
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-selected-apps-status",
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to get selected apps status");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<SelectedAppsStatus>(element.GetRawText())
                   ?? new SelectedAppsStatus { Message = "Failed to parse result" };
        }

        return new SelectedAppsStatus { Message = response.Message };
    }

    /// <summary>
    /// Shutdown daemon
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync("shutdown", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _responseWatcher?.Dispose();
        _lock.Dispose();

        foreach (var tcs in _pendingCommands.Values)
            tcs.TrySetCanceled();
        foreach (var tcs in _pendingChallenges.Values)
            tcs.TrySetCanceled();

        _disposed = true;
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

#endregion

#region Crypto

/// <summary>
/// Secure credential exchange using ECDH + AES-GCM
/// The daemon uses raw EC points (uncompressed format: 04 || X || Y)
/// and derives the shared secret using SHA-256 hash of the raw ECDH x-coordinate
/// </summary>
public static class SecureCredentialExchange
{
    public static EncryptedCredentialResponse EncryptCredential(
        string challengeId,
        string serverPublicKeyBase64,
        string credential)
    {
        // Create client ECDH key pair
        using var clientEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        // Export client public key as raw EC point (uncompressed: 04 || X || Y)
        var clientParams = clientEcdh.ExportParameters(true); // true = include private key for derivation
        var clientPublicKeyRaw = new byte[65];
        clientPublicKeyRaw[0] = 0x04; // Uncompressed point indicator
        Array.Copy(clientParams.Q.X!, 0, clientPublicKeyRaw, 1, 32);
        Array.Copy(clientParams.Q.Y!, 0, clientPublicKeyRaw, 33, 32);

        // Import server's raw EC point
        var serverPublicKeyBytes = Convert.FromBase64String(serverPublicKeyBase64);
        if (serverPublicKeyBytes.Length != 65 || serverPublicKeyBytes[0] != 0x04)
        {
            throw new CryptographicException($"Invalid server public key format. Expected 65-byte uncompressed EC point, got {serverPublicKeyBytes.Length} bytes");
        }

        var serverParams = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = serverPublicKeyBytes[1..33],
                Y = serverPublicKeyBytes[33..65]
            }
        };

        using var serverEcdh = ECDiffieHellman.Create(serverParams);

        // Derive shared secret using SHA-256 hash (common approach for interoperability)
        // .NET's DeriveKeyMaterial already does SHA-256 internally
        var sharedSecret = clientEcdh.DeriveKeyMaterial(serverEcdh.PublicKey);

        // Encrypt credential with AES-GCM using first 32 bytes as key
        using var aes = new AesGcm(sharedSecret[..32], 16);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var plaintext = Encoding.UTF8.GetBytes(credential);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedCredentialResponse
        {
            ChallengeId = challengeId,
            ClientPublicKey = Convert.ToBase64String(clientPublicKeyRaw),
            EncryptedCredential = Convert.ToBase64String(ciphertext),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag)
        };
    }

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
