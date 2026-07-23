using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Models;

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
    // Non-generic ILogger so the owning daemon service's real logger flows through unchanged: the caller
    // holds an ILogger<TSomeDaemonService>, and a generic-typed field here would force a category cast
    // that silently yields null (dropping every drain timeout/fault warning this client emits).
    private readonly ILogger? _logger;
    private readonly string? _sharedSecret;
    private Socket? _socket;
    private NetworkStream? _stream;
    private Task? _receiveTask;
    private CancellationTokenSource? _receiveCts;
    private bool _disposed;
    private volatile bool _isAuthenticated;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly object _transportSync = new();
    private readonly DaemonClientConnectionLifecycle _connectionLifecycle = new();

    // Tracks fire-and-forget ProcessEventAsync tasks so teardown can drain in-flight event callbacks
    // before disposal (see DaemonEventDrainTracker / DrainEventsAsync).
    private readonly DaemonEventDrainTracker _eventDrain;

    // Pending command responses keyed by request ID
    private readonly ConcurrentDictionary<string, PendingDaemonCommand> _pendingCommands = new();

    // Queue for credential challenges received via socket
    private readonly ConcurrentQueue<CredentialChallenge> _challengeQueue = new();
    private TaskCompletionSource<CredentialChallenge>? _challengeWaiter;

    // True while the CURRENT _challengeWaiter belongs to an in-flight StartLoginAsync login command.
    // The slot is shared between the login command and the challenge poll; without ownership a poll
    // could take over the slot and receive the login's challenge while the login orphans to its
    // timeout. Guarded by _challengeLock; set and cleared only by StartLoginAsync.
    private bool _challengeWaiterOwnedByLogin;
    private readonly object _challengeLock = new();

    public event Func<CredentialChallenge, Task>? OnCredentialChallenge;
    public event Func<DaemonStatus, Task>? OnStatusUpdate;
    public event Func<SocketPrefillProgress, Task>? OnProgressUpdate;
    public event Func<string, Task>? OnError;
    public event Func<Task>? OnDisconnected;

    public bool IsConnected => _connectionLifecycle.IsConnected;
    public bool IsAuthenticated => IsConnected && _isAuthenticated;

    /// <summary>
    /// HKDF info string for credential encryption. Must match the daemon's implementation.
    /// </summary>
    public string HkdfInfo { get; set; } = "SteamPrefill-Credential-Encryption";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TcpDaemonClient(string host, int port, string? sharedSecret = null, ILogger? logger = null)
    {
        _host = host;
        _port = port;
        _logger = logger;
        _eventDrain = new DaemonEventDrainTracker(_logger);

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
        if (IsConnected)
            return;

        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            if (IsConnected)
                return;

            await ConnectCoreAsync(cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await DisconnectCoreAsync();

        _logger?.LogInformation("Connecting to daemon TCP endpoint at {Host}:{Port}", _host, _port);

        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _connectionLifecycle.CreateGeneration();
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            NetworkStream? stream = null;
            CancellationTokenSource? receiveCts = null;
            var published = false;
            try
            {
                await socket.ConnectAsync(_host, _port, cancellationToken);
                stream = new NetworkStream(socket, ownsSocket: false);
                receiveCts = new CancellationTokenSource();

                lock (_transportSync)
                {
                    _socket = socket;
                    _stream = stream;
                    _receiveCts = receiveCts;
                    _isAuthenticated = false;
                    _connectionLifecycle.MarkConnected(generation);
                    _receiveTask = Task.Run(
                        () => ReceiveLoopAsync(stream, generation, receiveCts.Token),
                        CancellationToken.None);
                    published = true;
                }

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
            catch (OperationCanceledException)
            {
                if (published)
                {
                    await DisconnectCoreAsync();
                }
                else
                {
                    receiveCts?.Dispose();
                    stream?.Dispose();
                    socket.Dispose();
                }

                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                if (published)
                {
                    await DisconnectCoreAsync();
                }
                else
                {
                    receiveCts?.Dispose();
                    stream?.Dispose();
                    socket.Dispose();
                }

                await Task.Delay(200, cancellationToken);
            }
            catch
            {
                if (published)
                {
                    await DisconnectCoreAsync();
                }
                else
                {
                    receiveCts?.Dispose();
                    stream?.Dispose();
                    socket.Dispose();
                }

                throw;
            }
        }

        throw new SocketException((int)SocketError.ConnectionRefused);
    }

    private async Task AuthenticateAsync(CancellationToken cancellationToken)
    {
        _logger?.LogDebug("Authenticating with daemon...");

        var response = await SendCoreAsync("auth", new Dictionary<string, string>
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

    private async Task DisconnectCoreAsync()
    {
        var generation = _connectionLifecycle.MarkDisconnected();
        Socket? socket;
        NetworkStream? stream;
        Task? receiveTask;
        CancellationTokenSource? receiveCts;
        lock (_transportSync)
        {
            _isAuthenticated = false;
            socket = _socket;
            stream = _stream;
            receiveTask = _receiveTask;
            receiveCts = _receiveCts;
            _socket = null;
            _stream = null;
            _receiveTask = null;
            _receiveCts = null;
        }

        receiveCts?.Cancel();
        stream?.Dispose();
        socket?.Dispose();

        if (receiveTask != null)
        {
            try
            {
                await receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        receiveCts?.Dispose();
        if (generation != 0)
        {
            DaemonPendingCommandRegistry.FailGeneration(
                _pendingCommands,
                generation,
                new IOException("Daemon TCP connection disconnected while waiting for response"));
        }
    }

    private async Task ReceiveLoopAsync(
        NetworkStream stream,
        long generation,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested
                   && _connectionLifecycle.CurrentGeneration == generation)
            {
                var bytesRead = await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
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
                bytesRead = await ReadExactlyAsync(stream, messageBuffer, cancellationToken);
                if (bytesRead == 0)
                {
                    _logger?.LogWarning("Daemon TCP connection disconnected while reading message");
                    break;
                }

                var json = Encoding.UTF8.GetString(messageBuffer);
                _logger?.LogDebug("Received {Bytes}-byte message from daemon", json.Length);

                if (_connectionLifecycle.CurrentGeneration == generation)
                {
                    ProcessMessage(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Transport disposal can surface as ObjectDisposedException instead of cancellation.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error receiving from daemon TCP connection");
        }
        finally
        {
            if (_connectionLifecycle.TryMarkDisconnected(generation))
            {
                Socket? socket = null;
                CancellationTokenSource? receiveCts = null;
                lock (_transportSync)
                {
                    _isAuthenticated = false;
                    if (ReferenceEquals(_stream, stream))
                    {
                        socket = _socket;
                        receiveCts = _receiveCts;
                        _socket = null;
                        _stream = null;
                        _receiveTask = null;
                        _receiveCts = null;
                    }
                }

                stream.Dispose();
                socket?.Dispose();
                receiveCts?.Dispose();

                var pendingCount = DaemonPendingCommandRegistry.FailGeneration(
                    _pendingCommands,
                    generation,
                    new IOException("Daemon TCP connection disconnected while waiting for response"));
                if (pendingCount > 0)
                {
                    _logger?.LogWarning(
                        "Failing {Count} pending commands due to TCP disconnection",
                        pendingCount);
                }

                _eventDrain.TryTrack(() => DaemonEventDispatch.InvokeAllAsync(OnDisconnected, _logger));
            }
        }
    }

    internal void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                var messageType = JsonSerializer.Deserialize<DaemonMessageType>(typeElement.GetRawText(), _jsonOptions);

                if (messageType != DaemonMessageType.Unknown)
                {
                    // Atomic admission: TryTrack starts + tracks the callback under the drain lock, or
                    // rejects it (callback never runs) once teardown has begun draining - so no event can
                    // slip past DrainAsync's snapshot and run after this client is disposed.
                    _eventDrain.TryTrack(() => ProcessEventAsync(messageType, root));
                    return;
                }
            }

            if (root.TryGetProperty("id", out var idElement))
            {
                var response = JsonSerializer.Deserialize<CommandResponse>(json, _jsonOptions);
                if (response != null && _pendingCommands.TryRemove(response.Id, out var pending))
                {
                    pending.Completion.TrySetResult(response);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to process message from daemon");
        }
    }

    private async Task ProcessEventAsync(DaemonMessageType messageType, JsonElement root)
    {
        try
        {
            switch (messageType)
            {
                case DaemonMessageType.CredentialChallenge:
                    if (root.TryGetProperty("data", out var challengeData))
                    {
                        var challenge = JsonSerializer.Deserialize<CredentialChallenge>(challengeData.GetRawText(), _jsonOptions);
                        if (challenge != null)
                        {
                            _logger?.LogInformation("Received credential challenge: {Type}", challenge.CredentialType);
                            await HandleCredentialChallengeAsync(challenge);
                        }
                    }
                    break;

                case DaemonMessageType.Progress:
                    if (root.TryGetProperty("data", out var progressData))
                    {
                        var progress = JsonSerializer.Deserialize<SocketPrefillProgress>(progressData.GetRawText(), _jsonOptions);
                        if (progress != null)
                        {
                            await DaemonEventDispatch.InvokeAllAsync(OnProgressUpdate, progress, _logger);
                        }
                    }
                    break;

                case DaemonMessageType.AuthState:
                    if (root.TryGetProperty("data", out var authData))
                    {
                        var state = authData.TryGetProperty("state", out var stateElem) ? stateElem.GetString() : null;
                        var message = authData.TryGetProperty("message", out var msgElem) ? msgElem.GetString() : null;
                        var displayName = authData.TryGetProperty("displayName", out var dnElem) ? dnElem.GetString() : null;
                        _logger?.LogInformation("Auth state changed: {State} - {Message}", state, message);
                        await DaemonEventDispatch.InvokeAllAsync(OnStatusUpdate, new DaemonStatus
                        {
                            Status = state ?? "unknown",
                            Message = message,
                            DisplayName = displayName,
                            Timestamp = DateTime.UtcNow
                        }, _logger);
                    }
                    break;

                case DaemonMessageType.StatusUpdate:
                    var status = JsonSerializer.Deserialize<DaemonStatus>(root.GetRawText(), _jsonOptions);
                    if (status != null)
                    {
                        await DaemonEventDispatch.InvokeAllAsync(OnStatusUpdate, status, _logger);
                    }
                    break;

                default:
                    _logger?.LogDebug("Received unhandled daemon message type: {MessageType}", messageType);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to process event of type {MessageType}", messageType);
            await DaemonEventDispatch.InvokeAllAsync(OnError, ex.Message, _logger);
        }
    }

    /// <summary>
    /// Drains in-flight fire-and-forget event callbacks (bounded) and rejects new ones, so a teardown can
    /// guarantee no event writes/broadcasts after it returns. See <see cref="DaemonEventDrainTracker"/>.
    /// </summary>
    public Task DrainEventsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _eventDrain.DrainAsync(timeout, cancellationToken);

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

        await DaemonEventDispatch.InvokeAllAsync(OnCredentialChallenge, challenge, _logger);
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
        return await SendCoreAsync(type, parameters, timeout, cancellationToken);
    }

    private async Task<CommandResponse> SendCoreAsync(
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

        var generation = _connectionLifecycle.CurrentGeneration;
        if (generation == 0)
        {
            throw new IOException("Not connected to daemon");
        }

        var pending = new PendingDaemonCommand(generation);
        _pendingCommands[command.Id] = pending;

        try
        {
            var json = JsonSerializer.Serialize(command, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                NetworkStream stream;
                lock (_transportSync)
                {
                    if (_connectionLifecycle.CurrentGeneration != generation || _stream == null)
                    {
                        throw new IOException("Daemon connection changed before the command was sent");
                    }

                    stream = _stream;
                }

                await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);

                _logger?.LogDebug("Sent command: {Type} ({Id})", type, command.Id);
            }
            finally
            {
                _sendLock.Release();
            }

            using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(
                () => pending.Completion.TrySetCanceled(linkedCts.Token));

            return await pending.Completion.Task;
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
                    AuthExpiryUtc = DaemonStatus.ParseAuthExpiry(element),
                    AccountDisplayName = DaemonStatus.ParseAccountDisplayName(element),
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
            _challengeWaiterOwnedByLogin = true;
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
                // Release ownership; clear the slot only if it still holds this login's waiter (the
                // challenge dispatch or ClearPendingChallenges may already have consumed it).
                if (ReferenceEquals(_challengeWaiter, challengeTcs))
                {
                    _challengeWaiter = null;
                }
                _challengeWaiterOwnedByLogin = false;
            }
        }
    }

    public async Task ProvideCredentialAsync(
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        var encrypted = SecureCredentialExchange.Encrypt(
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

    public async Task<CredentialChallenge?> GetAutoLoginChallengeAsync(
        string sessionId,
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
            var response = await SendCommandAsync(
                "get-auto-login-challenge",
                new Dictionary<string, string> { ["sessionId"] = sessionId },
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken);

            var fromResponse = CredentialChallenge.TryParseFromResponse(response, _jsonOptions);
            if (fromResponse != null)
            {
                return fromResponse;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    public async Task<bool> ProvideAutoLoginAsync(
        string sessionId,
        string username,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var challenge = await GetAutoLoginChallengeAsync(sessionId, cancellationToken);
        if (challenge == null)
        {
            _logger?.LogWarning("Failed to obtain auto-login challenge for session {SessionId}", sessionId);
            return false;
        }

        var payload = JsonSerializer.Serialize(new AutoLoginPayload
        {
            Username = username,
            RefreshToken = refreshToken
        }, _jsonOptions);

        var encrypted = SecureCredentialExchange.Encrypt(
            challenge.ChallengeId,
            challenge.ServerPublicKey,
            payload,
            HkdfInfo);

        var response = await SendCommandAsync("provide-auto-login", new Dictionary<string, string>
        {
            ["sessionId"] = sessionId,
            ["challengeId"] = encrypted.ChallengeId,
            ["clientPublicKey"] = encrypted.ClientPublicKey,
            ["encryptedCredential"] = encrypted.EncryptedCredential,
            ["nonce"] = encrypted.Nonce,
            ["tag"] = encrypted.Tag
        }, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

        return response.Success;
    }

    public async Task<bool> ProvideEpicAutoLoginAsync(
        string sessionId,
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var challenge = await GetAutoLoginChallengeAsync(sessionId, cancellationToken);
        if (challenge == null)
        {
            _logger?.LogWarning("Failed to obtain auto-login challenge for session {SessionId}", sessionId);
            return false;
        }

        var payload = JsonSerializer.Serialize(new EpicAutoLoginPayload
        {
            RefreshToken = refreshToken
        }, _jsonOptions);

        var encrypted = SecureCredentialExchange.Encrypt(
            challenge.ChallengeId,
            challenge.ServerPublicKey,
            payload,
            HkdfInfo);

        var response = await SendCommandAsync("provide-auto-login", new Dictionary<string, string>
        {
            ["sessionId"] = sessionId,
            ["challengeId"] = encrypted.ChallengeId,
            ["clientPublicKey"] = encrypted.ClientPublicKey,
            ["encryptedCredential"] = encrypted.EncryptedCredential,
            ["nonce"] = encrypted.Nonce,
            ["tag"] = encrypted.Tag
        }, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

        return response.Success;
    }

    public async Task<bool> ProvideXboxAutoLoginAsync(
        string sessionId,
        string refreshToken,
        string deviceKeyPkcs8,
        CancellationToken cancellationToken = default)
    {
        var challenge = await GetAutoLoginChallengeAsync(sessionId, cancellationToken);
        if (challenge == null)
        {
            _logger?.LogWarning("Failed to obtain auto-login challenge for session {SessionId}", sessionId);
            return false;
        }

        var payload = JsonSerializer.Serialize(new XboxAutoLoginPayload
        {
            RefreshToken = refreshToken,
            DeviceKeyPkcs8 = deviceKeyPkcs8
        }, _jsonOptions);

        var encrypted = SecureCredentialExchange.Encrypt(
            challenge.ChallengeId,
            challenge.ServerPublicKey,
            payload,
            HkdfInfo);

        var response = await SendCommandAsync("provide-auto-login", new Dictionary<string, string>
        {
            ["sessionId"] = sessionId,
            ["challengeId"] = encrypted.ChallengeId,
            ["clientPublicKey"] = encrypted.ClientPublicKey,
            ["encryptedCredential"] = encrypted.EncryptedCredential,
            ["nonce"] = encrypted.Nonce,
            ["tag"] = encrypted.Tag
        }, timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

        return response.Success;
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

            // An in-flight login command owns the shared waiter slot. Its challenge belongs to the
            // login caller; taking over the slot here would hand that challenge to this poll and
            // orphan the login until its timeout. Report "no challenge available" instead - once the
            // challenge arrives it is published via the event channel/manager cache, which the next
            // poll reads.
            if (_challengeWaiterOwnedByLogin)
            {
                return null;
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
                // Clear only this poll's own waiter: a login command may have installed ITS waiter
                // after this poll's (superseding it) - clobbering the slot to null here would strand
                // that login's challenge in the queue until the login timed out.
                if (ReferenceEquals(_challengeWaiter, challengeTcs))
                {
                    _challengeWaiter = null;
                }
            }
        }
    }

    // Existing callers (the interactive modal's cancel) keep the historical best-effort contract:
    // errors are swallowed via the outcome variant below and the result is discarded.
    public Task CancelLoginAsync(CancellationToken cancellationToken = default)
        => CancelLoginWithOutcomeAsync(cancellationToken);

    public async Task<bool> CancelLoginWithOutcomeAsync(CancellationToken cancellationToken = default)
    {
        ClearPendingChallenges();
        try
        {
            var response = await SendCommandAsync("cancel-login", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
            return response.Success;
        }
        catch (Exception ex)
        {
            // Unacknowledged: the daemon may still have a login in flight. Callers that need
            // certainty read the false return; best-effort callers ignore it (the historical
            // swallow behavior).
            _logger?.LogWarning(ex, "cancel-login command was not acknowledged by the daemon");
            return false;
        }
    }

    public async Task CancelPrefillAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
            "cancel-prefill",
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellationToken);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                response.Error ?? response.Message ?? "Daemon rejected prefill cancellation.");
        }
    }

    /// <summary>
    /// Log out and forget the daemon's stored account in place. See <see cref="IDaemonClient.LogoutAsync"/>
    /// for the caveat: an un-updated steam/epic image also reports success here, but without actually
    /// deleting the stored account file - this method has no way to distinguish that from a true
    /// success, so callers can only rely on the response for genuine failures (socket errors, timeouts).
    /// </summary>
    public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var outcome = await LogoutWithReasonAsync(cancellationToken);
        return outcome.Success;
    }

    /// <inheritdoc cref="IDaemonClient.LogoutWithReasonAsync"/>
    public async Task<LogoutOutcome> LogoutWithReasonAsync(CancellationToken cancellationToken = default)
    {
        // 15s: see SocketDaemonClient.LogoutWithReasonAsync's comment - linked with the caller's own
        // 15s CTS (TryBestEffortLogoutAsync), both comfortably above the daemon's up-to-8s
        // LogoutLoginTaskTimeout unwind budget when logout races an in-flight login.
        var response = await SendCommandAsync("logout", timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);
        return new LogoutOutcome(response.Success, response.RequiresLogin == true);
    }

    public async Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-owned-games", cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to get owned games");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<List<OwnedGame>>(element.GetRawText(), _jsonOptions) ?? new List<OwnedGame>();
        }

        return new List<OwnedGame>();
    }

    /// <summary>
    /// Get CDN info for owned games.
    /// </summary>
    public async Task<List<CdnInfo>> GetCdnInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-cdn-info",
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to get CDN info");

        if (response.Data is JsonElement element)
        {
            var result = JsonSerializer.Deserialize<CdnInfoResult>(element.GetRawText(), _jsonOptions);
            return result?.Apps ?? new List<CdnInfo>();
        }

        return new List<CdnInfo>();
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
            parameters["cachedDepots"] = JsonSerializer.Serialize(cachedDepots, _jsonOptions);
            _logger?.LogInformation("Sending {Count} cached depot manifests to daemon", cachedDepots.Count);
        }

        var response = await SendCommandAsync("prefill", parameters,
            timeout: TimeSpan.FromHours(24),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Prefill failed");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<PrefillResult>(element.GetRawText(), _jsonOptions)
                   ?? new PrefillResult { Success = false, ErrorMessage = "Failed to parse result" };
        }

        return new PrefillResult { Success = true };
    }

    public async Task<ClearCacheResult> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("clear-cache", cancellationToken: cancellationToken);

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<ClearCacheResult>(element.GetRawText(), _jsonOptions)
                   ?? new ClearCacheResult { Success = false, Message = "Failed to parse result" };
        }

        return new ClearCacheResult { Success = response.Success, Message = response.Message };
    }

    public async Task<ClearCacheResult> GetCacheInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync("get-cache-info", cancellationToken: cancellationToken);

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<ClearCacheResult>(element.GetRawText(), _jsonOptions)
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
            return JsonSerializer.Deserialize<SelectedAppsStatus>(element.GetRawText(), _jsonOptions)
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
            ["cachedDepots"] = JsonSerializer.Serialize(cachedDepots, _jsonOptions)
        };

        var response = await SendCommandAsync("check-cache-status", parameters,
            timeout: TimeSpan.FromMinutes(10),
            cancellationToken: cancellationToken);

        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "Failed to check cache status");

        if (response.Data is JsonElement element)
        {
            return JsonSerializer.Deserialize<CacheStatusResult>(element.GetRawText(), _jsonOptions)
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

        DisconnectCoreAsync().GetAwaiter().GetResult();
        DaemonPendingCommandRegistry.FailAll(
            _pendingCommands,
            new ObjectDisposedException(nameof(TcpDaemonClient)));
        _sendLock.Dispose();
        _connectLock.Dispose();
        _disposed = true;
    }
}
