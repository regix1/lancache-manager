using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Unix Domain Socket client for communicating with SteamPrefill daemon.
/// Uses length-prefixed JSON protocol for reliable bidirectional communication.
/// Protocol: 4-byte little-endian length prefix + JSON message body
/// </summary>
public sealed class SocketDaemonClient : IDaemonClient
{
    private readonly string _socketPath;
    private readonly ILogger<SocketDaemonClient>? _logger;
    private readonly string? _sharedSecret;
    private Socket? _socket;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private CancellationTokenSource? _receiveCts;
    private bool _disposed;
    private bool _isAuthenticated;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Pending command responses keyed by request ID
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> _pendingCommands = new();

    // Queue for credential challenges received via socket
    private readonly ConcurrentQueue<CredentialChallenge> _challengeQueue = new();
    private TaskCompletionSource<CredentialChallenge>? _challengeWaiter;
    private readonly object _challengeLock = new();

    /// <summary>
    /// Event raised when a credential challenge is received.
    /// </summary>
    public event Func<CredentialChallenge, Task>? OnCredentialChallenge;

    /// <summary>
    /// Event raised when daemon status changes.
    /// </summary>
    public event Func<DaemonStatus, Task>? OnStatusUpdate;

    /// <summary>
    /// Event raised when a progress update is received.
    /// </summary>
    public event Func<SocketPrefillProgress, Task>? OnProgressUpdate;

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event Func<string, Task>? OnError;

    /// <summary>
    /// Event raised when connection is lost.
    /// </summary>
    public event Func<Task>? OnDisconnected;

    /// <summary>
    /// Whether currently connected to the daemon.
    /// </summary>
    public bool IsConnected => _socket?.Connected == true;

    /// <summary>
    /// Whether the client has been authenticated with the daemon.
    /// </summary>
    public bool IsAuthenticated => _isAuthenticated;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SocketDaemonClient(string socketPath, ILogger<SocketDaemonClient>? logger = null)
    {
        _socketPath = socketPath;
        _logger = logger;

        // Read optional shared secret for socket authentication
        _sharedSecret = Environment.GetEnvironmentVariable("PREFILL_SOCKET_SECRET");
        if (!string.IsNullOrEmpty(_sharedSecret))
        {
            _logger?.LogInformation("Socket authentication configured via PREFILL_SOCKET_SECRET");
        }
    }

    /// <summary>
    /// Connects to the daemon socket.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
    }

    /// <summary>
    /// Ensure connected to the daemon.
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_socket?.Connected == true)
            return;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (_socket?.Connected == true)
                return;

            await ConnectInternalAsync(cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        // Clean up previous connection if any
        await DisconnectInternalAsync();

        _logger?.LogInformation("Connecting to daemon socket at {SocketPath}", _socketPath);

        // Wait for socket file to exist (daemon may still be starting)
        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(_socketPath) && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (!File.Exists(_socketPath))
        {
            throw new FileNotFoundException($"Daemon socket not found at {_socketPath}");
        }

        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken);
            _stream = new NetworkStream(_socket, ownsSocket: false);

            // Start receiving messages
            _receiveCts = new CancellationTokenSource();
            _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

            _logger?.LogInformation("Connected to daemon socket");

            // Authenticate if shared secret is configured
            if (!string.IsNullOrEmpty(_sharedSecret))
            {
                await AuthenticateAsync(cancellationToken);
            }
            else
            {
                _isAuthenticated = true; // No auth required
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to connect to daemon socket at {SocketPath}", _socketPath);
            _socket?.Dispose();
            _socket = null;
            throw;
        }
    }

    /// <summary>
    /// Authenticates with the daemon using the shared secret.
    /// </summary>
    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Authenticating with daemon...");

        var response = await SendCommandInternalAsync("auth", new Dictionary<string, string>
        {
            ["secret"] = _sharedSecret!
        }, TimeSpan.FromSeconds(10), cancellationToken);

        if (!response.Success)
        {
            _isAuthenticated = false;
            throw new UnauthorizedAccessException($"Socket authentication failed: {response.Error}");
        }

        _isAuthenticated = true;
        _logger?.LogInformation("Socket authentication successful");
    }

    private async Task DisconnectInternalAsync()
    {
        _isAuthenticated = false;

        if (_receiveCts != null)
        {
            _receiveCts.Cancel();
            try
            {
                if (_receiveTask != null)
                    await _receiveTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            _receiveCts.Dispose();
            _receiveCts = null;
        }

        _stream?.Dispose();
        _stream = null;

        if (_socket != null)
        {
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch
            {
                // Ignore
            }
            _socket.Dispose();
            _socket = null;
        }

        // Cancel all pending commands
        foreach (var kvp in _pendingCommands)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingCommands.Clear();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested && _stream != null)
            {
                // Read length prefix (4 bytes, little-endian)
                var bytesRead = await ReadExactlyAsync(_stream, lengthBuffer, cancellationToken);
                if (bytesRead == 0)
                {
                    _logger?.LogWarning("Daemon socket disconnected");
                    break;
                }

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > 10 * 1024 * 1024) // Max 10MB
                {
                    _logger?.LogWarning("Invalid message length from daemon: {Length}", length);
                    break;
                }

                // Read message
                var messageBuffer = new byte[length];
                bytesRead = await ReadExactlyAsync(_stream, messageBuffer, cancellationToken);
                if (bytesRead == 0)
                {
                    _logger?.LogWarning("Daemon socket disconnected while reading message");
                    break;
                }

                var json = Encoding.UTF8.GetString(messageBuffer);
                _logger?.LogDebug("Received from daemon: {Json}", json.Length > 500 ? json[..500] + "..." : json);

                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error receiving from daemon socket");
        }
        finally
        {
            if (OnDisconnected != null)
            {
                _ = OnDisconnected.Invoke();
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if it's an event (has "type" at root level for socket events)
            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();

                // Socket events from daemon: credential-challenge, progress, auth-state, status-update
                if (type == "credential-challenge" || type == "progress" || type == "auth-state" || type == "status-update")
                {
                    _ = ProcessEventAsync(type, root);
                    return;
                }
            }

            // Check if it's a command response (has "id" property)
            if (root.TryGetProperty("id", out var idElement))
            {
                var response = JsonSerializer.Deserialize<CommandResponse>(json, JsonOptions);
                if (response != null && _pendingCommands.TryRemove(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to process message from daemon");
        }
    }

    private async Task ProcessEventAsync(string? type, JsonElement root)
    {
        try
        {
            switch (type)
            {
                case "credential-challenge":
                    if (root.TryGetProperty("data", out var challengeData))
                    {
                        var challenge = JsonSerializer.Deserialize<CredentialChallenge>(challengeData.GetRawText(), JsonOptions);
                        if (challenge != null)
                        {
                            _logger?.LogInformation("Received credential challenge: {Type}", challenge.CredentialType);
                            await HandleCredentialChallengeAsync(challenge);
                        }
                    }
                    break;

                case "progress":
                    if (root.TryGetProperty("data", out var progressData))
                    {
                        var progress = JsonSerializer.Deserialize<SocketPrefillProgress>(progressData.GetRawText(), JsonOptions);
                        if (progress != null && OnProgressUpdate != null)
                        {
                            await OnProgressUpdate.Invoke(progress);
                        }
                    }
                    break;

                case "auth-state":
                    if (root.TryGetProperty("data", out var authData))
                    {
                        var state = authData.TryGetProperty("state", out var stateElem) ? stateElem.GetString() : null;
                        var message = authData.TryGetProperty("message", out var msgElem) ? msgElem.GetString() : null;
                        _logger?.LogInformation("Auth state changed: {State} - {Message}", state, message);
                        // Convert auth state to DaemonStatus for the status event
                        if (OnStatusUpdate != null)
                        {
                            await OnStatusUpdate.Invoke(new DaemonStatus
                            {
                                Status = state ?? "unknown",
                                Message = message,
                                Timestamp = DateTime.UtcNow
                            });
                        }
                    }
                    break;

                case "status-update":
                    var status = JsonSerializer.Deserialize<DaemonStatus>(root.GetRawText(), JsonOptions);
                    if (status != null && OnStatusUpdate != null)
                    {
                        await OnStatusUpdate.Invoke(status);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to process event of type {Type}", type);
            if (OnError != null)
            {
                await OnError.Invoke(ex.Message);
            }
        }
    }

    private async Task HandleCredentialChallengeAsync(CredentialChallenge challenge)
    {
        lock (_challengeLock)
        {
            // If there's a waiter, give it the challenge directly
            if (_challengeWaiter != null)
            {
                _challengeWaiter.TrySetResult(challenge);
                _challengeWaiter = null;
            }
            else
            {
                // Queue it for later retrieval
                _challengeQueue.Enqueue(challenge);
            }
        }

        // Always fire the event
        if (OnCredentialChallenge != null)
        {
            await OnCredentialChallenge.Invoke(challenge);
        }
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
                return 0; // Connection closed
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// Send a command and wait for response.
    /// </summary>
    public async Task<CommandResponse> SendCommandAsync(
        string type,
        Dictionary<string, string>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await SendCommandInternalAsync(type, parameters, timeout, cancellationToken);
    }

    /// <summary>
    /// Internal method to send command without connection check.
    /// Used during authentication when we're already connected but not yet authenticated.
    /// </summary>
    private async Task<CommandResponse> SendCommandInternalAsync(
        string type,
        Dictionary<string, string>? parameters,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var command = new CommandRequest
        {
            Id = Guid.NewGuid().ToString(),
            Type = type,
            Parameters = parameters,
            CreatedAt = DateTime.UtcNow
        };

        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[command.Id] = tcs;

        try
        {
            // Serialize and send with length prefix
            var json = JsonSerializer.Serialize(command, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_stream == null)
                    throw new InvalidOperationException("Not connected to daemon");

                // Send length prefix (4 bytes, little-endian)
                await _stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
                // Send message body
                await _stream.WriteAsync(bytes, cancellationToken);
                await _stream.FlushAsync(cancellationToken);

                _logger?.LogDebug("Sent command: {Type} ({Id})", type, command.Id);
            }
            finally
            {
                _sendLock.Release();
            }

            // Wait for response with timeout
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        finally
        {
            _pendingCommands.TryRemove(command.Id, out _);
        }
    }

    /// <summary>
    /// Get the current daemon status.
    /// </summary>
    public async Task<DaemonStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendCommandAsync("status", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
            if (response.Success && response.Data is JsonElement element)
            {
                return new DaemonStatus
                {
                    Status = element.TryGetProperty("isLoggedIn", out var loggedIn) && loggedIn.GetBoolean() ? "logged-in" : "not-logged-in",
                    Message = element.TryGetProperty("isInitialized", out var init) && init.GetBoolean() ? "Initialized" : "Not initialized",
                    Timestamp = DateTime.UtcNow
                };
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get daemon status");
            return null;
        }
    }

    /// <summary>
    /// Start login process.
    /// </summary>
    public async Task<CredentialChallenge?> StartLoginAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // Clear any pending challenges
        ClearPendingChallenges();

        // Set up waiter before sending command
        var challengeTcs = new TaskCompletionSource<CredentialChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_challengeLock)
        {
            _challengeWaiter = challengeTcs;
        }

        try
        {
            // Send login command (don't await the response - it comes after auth is complete)
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendCommandAsync("login", timeout: TimeSpan.FromMinutes(10), cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Login command completed or failed");
                }
            }, cancellationToken);

            // Wait for credential challenge with timeout
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() => challengeTcs.TrySetCanceled());

            return await challengeTcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_challengeLock)
            {
                _challengeWaiter = null;
            }
        }
    }

    /// <summary>
    /// Provide encrypted credential in response to a challenge.
    /// </summary>
    public async Task ProvideCredentialAsync(
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var encrypted = SecureCredentialExchange.EncryptCredentialRaw(
            challenge.ChallengeId,
            challenge.ServerPublicKey,
            credential);

        await SendCommandAsync("provide-credential", new Dictionary<string, string>
        {
            ["challengeId"] = encrypted.ChallengeId,
            ["clientPublicKey"] = encrypted.ClientPublicKey,
            ["encryptedCredential"] = encrypted.EncryptedCredential,
            ["nonce"] = encrypted.Nonce,
            ["tag"] = encrypted.Tag
        }, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Wait for next credential challenge.
    /// </summary>
    public async Task<CredentialChallenge?> WaitForChallengeAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        // Check queue first
        if (_challengeQueue.TryDequeue(out var existingChallenge))
        {
            return existingChallenge;
        }

        var challengeTcs = new TaskCompletionSource<CredentialChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_challengeLock)
        {
            // Double-check queue after acquiring lock
            if (_challengeQueue.TryDequeue(out existingChallenge))
            {
                return existingChallenge;
            }
            _challengeWaiter = challengeTcs;
        }

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(() => challengeTcs.TrySetCanceled());

            return await challengeTcs.Task;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            lock (_challengeLock)
            {
                _challengeWaiter = null;
            }
        }
    }

    /// <summary>
    /// Cancel pending login.
    /// </summary>
    public async Task CancelLoginAsync(CancellationToken cancellationToken = default)
    {
        ClearPendingChallenges();
        try
        {
            await SendCommandAsync("cancel-login", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
        }
        catch
        {
            // Ignore errors during cancel
        }
    }

    /// <summary>
    /// Cancel running prefill operation.
    /// </summary>
    public async Task CancelPrefillAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync("cancel-prefill", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Get owned games.
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-owned-games", cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to get owned games");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<List<OwnedGame>>(element.GetRawText(), JsonOptions) ?? new List<OwnedGame>();
        }

        return new List<OwnedGame>();
    }

    /// <summary>
    /// Set selected apps for prefill.
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
    /// Start prefill operation.
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        bool all = false,
        bool recent = false,
        bool recentlyPurchased = false,
        int? top = null,
        bool force = false,
        List<string>? operatingSystems = null,
        int? maxConcurrency = null,
        List<CachedDepotInput>? cachedDepots = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>();
        if (all) parameters["all"] = "true";
        if (recent) parameters["recent"] = "true";
        if (recentlyPurchased) parameters["recentlyPurchased"] = "true";
        if (top.HasValue) parameters["top"] = top.Value.ToString();
        if (force) parameters["force"] = "true";
        if (maxConcurrency.HasValue && maxConcurrency.Value > 0)
            parameters["maxConcurrency"] = maxConcurrency.Value.ToString();
        // Always include all OS platforms by default
        parameters["os"] = operatingSystems != null && operatingSystems.Count > 0
            ? string.Join(",", operatingSystems)
            : "windows,linux,macos";

        // Pass cached depot manifests so daemon can skip up-to-date games
        if (cachedDepots != null && cachedDepots.Count > 0)
        {
            parameters["cachedDepots"] = JsonSerializer.Serialize(cachedDepots, JsonOptions);
            _logger?.LogInformation("Sending {Count} cached depot manifests to daemon", cachedDepots.Count);
        }

        var response = await SendCommandAsync("prefill", parameters,
            timeout: TimeSpan.FromHours(24),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Prefill failed");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<PrefillResult>(element.GetRawText(), JsonOptions)
                   ?? new PrefillResult { Success = false, ErrorMessage = "Failed to parse result" };
        }

        return new PrefillResult { Success = true };
    }

    /// <summary>
    /// Clear the temporary cache.
    /// </summary>
    public async Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("clear-cache", cancellationToken: cancellationToken);

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<ClearCacheResult>(element.GetRawText(), JsonOptions)
                   ?? new ClearCacheResult { Success = false, Message = "Failed to parse result" };
        }

        return new ClearCacheResult { Success = response.Success, Message = response.Message };
    }

    /// <summary>
    /// Get cache info.
    /// </summary>
    public async Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-cache-info", cancellationToken: cancellationToken);

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<ClearCacheResult>(element.GetRawText(), JsonOptions)
                   ?? new ClearCacheResult { Success = false, Message = "Failed to parse result" };
        }

        return new ClearCacheResult { Success = response.Success, Message = response.Message };
    }

    /// <summary>
    /// Get selected apps status with download sizes.
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(
        List<string>? operatingSystems = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["os"] = operatingSystems != null && operatingSystems.Count > 0
                ? string.Join(",", operatingSystems)
                : "windows,linux,macos"
        };

        var response = await SendCommandAsync("get-selected-apps-status", parameters,
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to get selected apps status");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<SelectedAppsStatus>(element.GetRawText(), JsonOptions)
                   ?? new SelectedAppsStatus { Message = "Failed to parse result" };
        }

        return new SelectedAppsStatus { Message = response.Message };
    }

    /// <summary>
    /// Shutdown daemon.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync("shutdown", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Clear pending challenges.
    /// </summary>
    public void ClearPendingChallenges()
    {
        lock (_challengeLock)
        {
            while (_challengeQueue.TryDequeue(out _)) { }
            _challengeWaiter?.TrySetCanceled();
            _challengeWaiter = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        DisconnectInternalAsync().GetAwaiter().GetResult();
        _sendLock.Dispose();
        _connectLock.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Progress update from prefill operation via socket.
/// Matches daemon's PrefillProgressUpdate class property names.
/// </summary>
public class SocketPrefillProgress
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("currentAppId")]
    public uint CurrentAppId { get; set; }

    [JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Depot manifest info for cache tracking - sent with app_completed events.
    /// </summary>
    [JsonPropertyName("depots")]
    public List<SocketDepotManifestInfo>? Depots { get; set; }
}

/// <summary>
/// Depot manifest info from socket progress updates.
/// </summary>
public class SocketDepotManifestInfo
{
    [JsonPropertyName("depotId")]
    public uint DepotId { get; set; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}
