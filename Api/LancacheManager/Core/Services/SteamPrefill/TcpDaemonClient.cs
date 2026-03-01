using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// TCP client for communicating with SteamPrefill daemon.
/// Uses length-prefixed JSON protocol for reliable bidirectional communication.
/// Protocol: 4-byte little-endian length prefix + JSON message body
/// </summary>
public sealed class TcpDaemonClient : IDaemonClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger<TcpDaemonClient>? _logger;
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

    public event Func<CredentialChallenge, Task>? OnCredentialChallenge;
    public event Func<DaemonStatus, Task>? OnStatusUpdate;
    public event Func<SocketPrefillProgress, Task>? OnProgressUpdate;
    public event Func<string, Task>? OnError;
    public event Func<Task>? OnDisconnected;

    public bool IsConnected => _socket?.Connected == true;
    public bool IsAuthenticated => _isAuthenticated;

    /// <summary>
    /// HKDF info string for credential encryption. Must match the daemon's implementation.
    /// </summary>
    public string HkdfInfo { get; set; } = "SteamPrefill-Credential-Encryption";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TcpDaemonClient(string host, int port, string? sharedSecret = null, ILogger<TcpDaemonClient>? logger = null)
    {
        _host = host;
        _port = port;
        _logger = logger;

        // Read shared secret for socket authentication
        _sharedSecret = sharedSecret;
        if (!string.IsNullOrEmpty(_sharedSecret))
        {
            _logger?.LogInformation("Socket authentication enabled for daemon client");
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_socket?.Connected == true)
            return;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
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
        await DisconnectInternalAsync();

        _logger?.LogInformation("Connecting to daemon TCP endpoint at {Host}:{Port}", _host, _port);

        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await _socket.ConnectAsync(_host, _port, cancellationToken);
                _stream = new NetworkStream(_socket, ownsSocket: false);

                _receiveCts = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(_receiveCts.Token);

                _logger?.LogInformation("Connected to daemon TCP endpoint");

                if (!string.IsNullOrEmpty(_sharedSecret))
                {
                    await AuthenticateAsync(cancellationToken);
                }
                else
                {
                    _isAuthenticated = true;
                }

                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        throw new SocketException((int)SocketError.ConnectionRefused);
    }

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
                var bytesRead = await ReadExactlyAsync(_stream, lengthBuffer, cancellationToken);
                if (bytesRead == 0)
                {
                    _logger?.LogWarning("Daemon TCP connection disconnected");
                    break;
                }

                var length = BitConverter.ToInt32(lengthBuffer, 0);
                if (length <= 0 || length > 10 * 1024 * 1024)
                {
                    _logger?.LogWarning("Invalid message length from daemon: {Length}", length);
                    break;
                }

                var messageBuffer = new byte[length];
                bytesRead = await ReadExactlyAsync(_stream, messageBuffer, cancellationToken);
                if (bytesRead == 0)
                {
                    _logger?.LogWarning("Daemon TCP connection disconnected while reading message");
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
            _logger?.LogError(ex, "Error receiving from daemon TCP connection");
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

            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();

                if (type == "credential-challenge" || type == "progress" || type == "auth-state" || type == "status-update")
                {
                    _ = ProcessEventAsync(type, root);
                    return;
                }
            }

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
                        var displayName = authData.TryGetProperty("displayName", out var dnElem) ? dnElem.GetString() : null;
                        _logger?.LogInformation("Auth state changed: {State} - {Message}", state, message);
                        if (OnStatusUpdate != null)
                        {
                            await OnStatusUpdate.Invoke(new DaemonStatus
                            {
                                Status = state ?? "unknown",
                                Message = message,
                                DisplayName = displayName,
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
            if (_challengeWaiter != null)
            {
                _challengeWaiter.TrySetResult(challenge);
                _challengeWaiter = null;
            }
            else
            {
                _challengeQueue.Enqueue(challenge);
            }
        }

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
                return 0;
            totalRead += read;
        }
        return totalRead;
    }

    public async Task<CommandResponse> SendCommandAsync(
        string type,
        Dictionary<string, string>? parameters = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(cancellationToken);
        return await SendCommandInternalAsync(type, parameters, timeout, cancellationToken);
    }

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
            var json = JsonSerializer.Serialize(command, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                if (_stream == null)
                    throw new InvalidOperationException("Not connected to daemon");

                await _stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
                await _stream.WriteAsync(bytes, cancellationToken);
                await _stream.FlushAsync(cancellationToken);

                _logger?.LogDebug("Sent command: {Type} ({Id})", type, command.Id);
            }
            finally
            {
                _sendLock.Release();
            }

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

    public async Task<CredentialChallenge?> StartLoginAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ClearPendingChallenges();

        var challengeTcs = new TaskCompletionSource<CredentialChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_challengeLock)
        {
            _challengeWaiter = challengeTcs;
        }

        try
        {
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

    public async Task ProvideCredentialAsync(
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var encrypted = SecureCredentialExchange.EncryptCredentialRaw(
            challenge.ChallengeId,
            challenge.ServerPublicKey,
            credential,
            HkdfInfo);

        await SendCommandAsync("provide-credential", new Dictionary<string, string>
        {
            ["challengeId"] = encrypted.ChallengeId,
            ["clientPublicKey"] = encrypted.ClientPublicKey,
            ["encryptedCredential"] = encrypted.EncryptedCredential,
            ["nonce"] = encrypted.Nonce,
            ["tag"] = encrypted.Tag
        }, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
    }

    public async Task<CredentialChallenge?> WaitForChallengeAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_challengeQueue.TryDequeue(out var existingChallenge))
        {
            return existingChallenge;
        }

        var challengeTcs = new TaskCompletionSource<CredentialChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_challengeLock)
        {
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

    public async Task CancelPrefillAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync("cancel-prefill", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
    }

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

    public async Task SetSelectedAppsAsync(List<string> appIds, CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("set-selected-apps", new Dictionary<string, string>
        {
            ["appIds"] = DaemonSerializer.SerializeAppIds(appIds)
        }, cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to set selected apps");
    }

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
        if (recentlyPurchased) parameters["recently_purchased"] = "true";
        if (top.HasValue) parameters["top"] = top.Value.ToString();
        if (force) parameters["force"] = "true";
        if (maxConcurrency.HasValue && maxConcurrency.Value > 0)
            parameters["maxConcurrency"] = maxConcurrency.Value.ToString();
        parameters["os"] = operatingSystems != null && operatingSystems.Count > 0
            ? string.Join(",", operatingSystems)
            : "windows,linux,macos";

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

    public async Task<CacheStatusResult> CheckCacheStatusAsync(
        List<CachedDepotInput> cachedDepots,
        CancellationToken cancellationToken = default)
    {
        if (cachedDepots == null || cachedDepots.Count == 0)
        {
            return new CacheStatusResult { Message = "No cached depots provided" };
        }

        var parameters = new Dictionary<string, string>
        {
            ["cachedDepots"] = JsonSerializer.Serialize(cachedDepots, JsonOptions)
        };

        var response = await SendCommandAsync("check-cache-status", parameters,
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to check cache status");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<CacheStatusResult>(element.GetRawText(), JsonOptions)
                   ?? new CacheStatusResult { Message = "Failed to parse result" };
        }

        return new CacheStatusResult { Message = response.Message };
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await SendCommandAsync("shutdown", timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
    }

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
