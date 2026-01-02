using System.Collections.Concurrent;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Application.SteamPrefill;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

/// <summary>
/// Manages Steam Prefill daemon Docker containers.
/// Each user session gets its own container with dedicated command/response directories.
/// Uses encrypted credential exchange (ECDH + AES-GCM) for secure authentication.
/// </summary>
public class SteamPrefillDaemonService : IHostedService, IDisposable
{
    private readonly ILogger<SteamPrefillDaemonService> _logger;
    private readonly IHubContext<PrefillDaemonHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, DaemonSession> _sessions = new();
    private DockerClient? _dockerClient;
    private Timer? _cleanupTimer;
    private bool _disposed;

    // Configuration defaults
    private const int DefaultSessionTimeoutMinutes = 120;
    private const string DefaultDockerImage = "ghcr.io/regix1/steam-prefill-daemon:latest";

    public SteamPrefillDaemonService(
        ILogger<SteamPrefillDaemonService> logger,
        IHubContext<PrefillDaemonHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _hubContext = hubContext;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SteamPrefillDaemonService starting...");

        // Initialize Docker client
        try
        {
            Uri dockerUri;
            if (OperatingSystem.IsWindows())
            {
                dockerUri = new Uri("npipe://./pipe/docker_engine");
            }
            else
            {
                dockerUri = new Uri("unix:///var/run/docker.sock");
            }

            _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
            _logger.LogInformation("Docker client initialized: {Endpoint}", dockerUri);

            // Ensure image is available
            await EnsureImageExistsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Docker client. Container management will be unavailable.");
        }

        // Start cleanup timer (every minute)
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        _logger.LogInformation("SteamPrefillDaemonService started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SteamPrefillDaemonService stopping...");

        _cleanupTimer?.Change(Timeout.Infinite, 0);

        // Terminate all active sessions
        var sessions = _sessions.Values.ToList();
        foreach (var session in sessions)
        {
            await TerminateSessionAsync(session.Id, "Service shutdown");
        }

        _logger.LogInformation("SteamPrefillDaemonService stopped");
    }

    /// <summary>
    /// Creates a new daemon session for a user.
    /// Spawns a Docker container with dedicated command/response directories.
    /// </summary>
    public async Task<DaemonSession> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_dockerClient == null)
        {
            throw new InvalidOperationException("Docker client not initialized. Cannot create session.");
        }

        // Check if user already has an active session
        var existingSession = _sessions.Values.FirstOrDefault(s => s.UserId == userId && s.Status == DaemonSessionStatus.Active);
        if (existingSession != null)
        {
            throw new InvalidOperationException($"User already has an active session: {existingSession.Id}");
        }

        var sessionId = Guid.NewGuid().ToString("N")[..16];
        var basePath = GetDaemonBasePath();
        var sessionPath = Path.Combine(basePath, "sessions", sessionId);
        var commandsDir = Path.Combine(sessionPath, "commands");
        var responsesDir = Path.Combine(sessionPath, "responses");

        // Create directories
        Directory.CreateDirectory(commandsDir);
        Directory.CreateDirectory(responsesDir);

        _logger.LogInformation("Creating daemon container for session {SessionId}, user {UserId}", sessionId, userId);

        // Create and start container
        var containerName = $"prefill-daemon-{sessionId}";
        var imageName = GetImageName();

        var createResponse = await _dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Name = containerName,
                Image = imageName,
                Env = new List<string>
                {
                    $"PREFILL_COMMANDS_DIR=/commands",
                    $"PREFILL_RESPONSES_DIR=/responses"
                },
                HostConfig = new HostConfig
                {
                    Binds = new List<string>
                    {
                        $"{commandsDir}:/commands",
                        $"{responsesDir}:/responses"
                    },
                    AutoRemove = true
                }
            },
            cancellationToken);

        var containerId = createResponse.ID;
        _logger.LogInformation("Created container {ContainerId} for session {SessionId}", containerId, sessionId);

        // Start container
        var started = await _dockerClient.Containers.StartContainerAsync(containerId, null, cancellationToken);
        if (!started)
        {
            throw new InvalidOperationException($"Failed to start container {containerId}");
        }

        _logger.LogInformation("Started container {ContainerId} for session {SessionId}", containerId, sessionId);

        var session = new DaemonSession
        {
            Id = sessionId,
            UserId = userId,
            ContainerId = containerId,
            ContainerName = containerName,
            CommandsDir = commandsDir,
            ResponsesDir = responsesDir,
            ExpiresAt = DateTime.UtcNow.AddMinutes(GetSessionTimeoutMinutes())
        };

        // Create daemon client
        session.Client = new DaemonClient(commandsDir, responsesDir);

        // Start watching for status and challenge files
        StartStatusWatcher(session);

        _sessions[sessionId] = session;

        _logger.LogInformation("Created daemon session {SessionId} for user {UserId}", sessionId, userId);

        return session;
    }

    /// <summary>
    /// Gets the current status of a daemon session
    /// </summary>
    public async Task<DaemonStatus?> GetSessionStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return null;
        }

        return await session.Client.GetStatusAsync(cancellationToken);
    }

    /// <summary>
    /// Starts the login process for a daemon session.
    /// Returns a credential challenge if credentials are needed.
    /// </summary>
    public async Task<CredentialChallenge?> StartLoginAsync(string sessionId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        session.AuthState = DaemonAuthState.LoggingIn;
        await NotifyAuthStateChangeAsync(session);

        return await session.Client.StartLoginAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge
    /// </summary>
    public async Task ProvideCredentialAsync(
        string sessionId,
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        _logger.LogInformation("Providing encrypted {CredentialType} for session {SessionId}",
            challenge.CredentialType, sessionId);

        await session.Client.ProvideCredentialAsync(challenge, credential, cancellationToken);
    }

    /// <summary>
    /// Waits for the next credential challenge
    /// </summary>
    public async Task<CredentialChallenge?> WaitForChallengeAsync(
        string sessionId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.WaitForChallengeAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Gets owned games for a logged-in session
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.GetOwnedGamesAsync(cancellationToken);
    }

    /// <summary>
    /// Sets selected apps for prefill
    /// </summary>
    public async Task SetSelectedAppsAsync(string sessionId, List<uint> appIds, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        await session.Client.SetSelectedAppsAsync(appIds, cancellationToken);
    }

    /// <summary>
    /// Starts a prefill operation
    /// </summary>
    public async Task<PrefillResult> PrefillAsync(
        string sessionId,
        bool all = false,
        bool recent = false,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        session.IsPrefilling = true;
        await NotifyPrefillStateChangeAsync(session, "started");

        try
        {
            var result = await session.Client.PrefillAsync(all, recent, force, cancellationToken);
            await NotifyPrefillStateChangeAsync(session, result.Success ? "completed" : "failed");
            return result;
        }
        finally
        {
            session.IsPrefilling = false;
        }
    }

    /// <summary>
    /// Shuts down the daemon for a session
    /// </summary>
    public async Task ShutdownDaemonAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        try
        {
            await session.Client.ShutdownAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error shutting down daemon for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Terminates a session and cleans up resources
    /// </summary>
    public async Task TerminateSessionAsync(string sessionId, string reason = "User requested")
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        _logger.LogInformation("Terminating session {SessionId}: {Reason}", sessionId, reason);

        session.Status = DaemonSessionStatus.Terminated;
        session.EndedAt = DateTime.UtcNow;

        // Shutdown daemon via command first
        try
        {
            await session.Client.ShutdownAsync();
        }
        catch
        {
            // Ignore shutdown errors
        }

        // Stop and remove container
        if (_dockerClient != null && !string.IsNullOrEmpty(session.ContainerId))
        {
            try
            {
                await _dockerClient.Containers.StopContainerAsync(
                    session.ContainerId,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 5 });

                _logger.LogInformation("Stopped container {ContainerId} for session {SessionId}",
                    session.ContainerId, sessionId);
            }
            catch (DockerContainerNotFoundException)
            {
                // Container already removed (AutoRemove)
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping container {ContainerId}", session.ContainerId);
            }
        }

        // Notify subscribers
        await NotifySessionEndedAsync(session, reason);

        // Cleanup
        session.Client.Dispose();
        session.StatusWatcher?.Dispose();
        session.CancellationTokenSource.Cancel();
        session.CancellationTokenSource.Dispose();

        // Clean up directories
        try
        {
            var sessionDir = Path.GetDirectoryName(session.CommandsDir);
            if (sessionDir != null && Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cleaning up session directory for {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Gets a session by ID
    /// </summary>
    public DaemonSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Gets all active sessions
    /// </summary>
    public IEnumerable<DaemonSession> GetAllSessions()
    {
        return _sessions.Values.ToList();
    }

    /// <summary>
    /// Gets sessions for a specific user
    /// </summary>
    public IEnumerable<DaemonSession> GetUserSessions(string userId)
    {
        return _sessions.Values.Where(s => s.UserId == userId).ToList();
    }

    /// <summary>
    /// Adds a SignalR connection as a subscriber to session events
    /// </summary>
    public void AddSubscriber(string sessionId, string connectionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.SubscribedConnections.Add(connectionId);
        }
    }

    /// <summary>
    /// Removes a SignalR connection from all session subscriptions
    /// </summary>
    public void RemoveSubscriber(string connectionId)
    {
        foreach (var session in _sessions.Values)
        {
            session.SubscribedConnections.Remove(connectionId);
        }
    }

    private async Task EnsureImageExistsAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        var imageName = GetImageName();

        try
        {
            await _dockerClient.Images.InspectImageAsync(imageName, cancellationToken);
            _logger.LogDebug("Image exists: {ImageName}", imageName);
        }
        catch (DockerImageNotFoundException)
        {
            _logger.LogInformation("Pulling image: {ImageName}", imageName);

            await _dockerClient.Images.CreateImageAsync(
                new ImagesCreateParameters
                {
                    FromImage = imageName.Split(':')[0],
                    Tag = imageName.Contains(':') ? imageName.Split(':')[1] : "latest"
                },
                null,
                new Progress<JSONMessage>(msg =>
                {
                    if (!string.IsNullOrEmpty(msg.Status))
                    {
                        _logger.LogDebug("Pulling: {Status}", msg.Status);
                    }
                }),
                cancellationToken);

            _logger.LogInformation("Image pulled: {ImageName}", imageName);
        }
    }

    private void StartStatusWatcher(DaemonSession session)
    {
        session.StatusWatcher = new FileSystemWatcher(session.ResponsesDir, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        session.StatusWatcher.Created += async (sender, e) =>
        {
            try
            {
                await HandleResponseFileAsync(session, e.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling response file {Path}", e.FullPath);
            }
        };

        session.StatusWatcher.Changed += async (sender, e) =>
        {
            try
            {
                // Only handle daemon_status.json changes
                if (Path.GetFileName(e.FullPath) == "daemon_status.json")
                {
                    await HandleStatusChangeAsync(session, e.FullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling status change {Path}", e.FullPath);
            }
        };
    }

    private async Task HandleResponseFileAsync(DaemonSession session, string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        // Handle credential challenges
        if (fileName.StartsWith("auth_challenge_"))
        {
            await Task.Delay(50); // Ensure file is written

            try
            {
                var json = await File.ReadAllTextAsync(filePath);
                var challenge = JsonSerializer.Deserialize<CredentialChallenge>(json);

                if (challenge != null)
                {
                    // Update auth state based on credential type
                    session.AuthState = challenge.CredentialType switch
                    {
                        "password" => DaemonAuthState.PasswordRequired,
                        "2fa" => DaemonAuthState.TwoFactorRequired,
                        "steamguard" => DaemonAuthState.SteamGuardRequired,
                        "device-confirmation" => DaemonAuthState.DeviceConfirmationRequired,
                        _ => session.AuthState
                    };

                    await NotifyCredentialChallengeAsync(session, challenge);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing credential challenge from {Path}", filePath);
            }
        }
    }

    private async Task HandleStatusChangeAsync(DaemonSession session, string filePath)
    {
        await Task.Delay(50); // Ensure file is written

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var status = JsonSerializer.Deserialize<DaemonStatus>(json);

            if (status != null)
            {
                var previousAuthState = session.AuthState;

                // Update auth state based on status
                session.AuthState = status.Status switch
                {
                    "awaiting-login" => DaemonAuthState.NotAuthenticated,
                    "logged-in" => DaemonAuthState.Authenticated,
                    _ => session.AuthState
                };

                if (session.AuthState != previousAuthState)
                {
                    await NotifyAuthStateChangeAsync(session);
                }

                await NotifyStatusChangeAsync(session, status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing status from {Path}", filePath);
        }
    }

    private async Task NotifyAuthStateChangeAsync(DaemonSession session)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("AuthStateChanged", session.Id, session.AuthState.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify auth state to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("CredentialChallenge", session.Id, challenge);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify credential challenge to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("StatusChanged", session.Id, status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify status to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyPrefillStateChangeAsync(DaemonSession session, string state)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("PrefillStateChanged", session.Id, state);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify prefill state to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifySessionEndedAsync(DaemonSession session, string reason)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("SessionEnded", session.Id, reason);
            }
            catch
            {
                // Ignore
            }
        }
    }

    private void CleanupExpiredSessions(object? state)
    {
        var expiredSessions = _sessions.Values
            .Where(s => s.Status == DaemonSessionStatus.Active && DateTime.UtcNow > s.ExpiresAt)
            .ToList();

        foreach (var session in expiredSessions)
        {
            _logger.LogInformation("Session expired: {SessionId}", session.Id);
            _ = TerminateSessionAsync(session.Id, "Session expired");
        }
    }

    private string GetDaemonBasePath()
    {
        return _configuration["Prefill:DaemonBasePath"] ?? Path.Combine(Path.GetTempPath(), "steamprefill");
    }

    private string GetImageName()
    {
        return _configuration["Prefill:DockerImage"] ?? DefaultDockerImage;
    }

    private int GetSessionTimeoutMinutes()
    {
        return _configuration.GetValue<int>("Prefill:SessionTimeoutMinutes", DefaultSessionTimeoutMinutes);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _cleanupTimer?.Dispose();
        _dockerClient?.Dispose();

        foreach (var session in _sessions.Values)
        {
            session.Client.Dispose();
            session.StatusWatcher?.Dispose();
            session.CancellationTokenSource.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>
/// Represents a Steam Prefill daemon session
/// </summary>
public class DaemonSession
{
    public string Id { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string CommandsDir { get; init; } = string.Empty;
    public string ResponsesDir { get; init; } = string.Empty;
    public DaemonSessionStatus Status { get; set; } = DaemonSessionStatus.Active;
    public DaemonAuthState AuthState { get; set; } = DaemonAuthState.NotAuthenticated;
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public DateTime ExpiresAt { get; init; }

    public DaemonClient Client { get; set; } = null!;
    public FileSystemWatcher? StatusWatcher { get; set; }
    public HashSet<string> SubscribedConnections { get; } = new();
    public CancellationTokenSource CancellationTokenSource { get; } = new();
}

public enum DaemonSessionStatus
{
    Active,
    Terminated,
    Error
}

public enum DaemonAuthState
{
    NotAuthenticated,
    LoggingIn,
    PasswordRequired,
    TwoFactorRequired,
    SteamGuardRequired,
    DeviceConfirmationRequired,
    Authenticated
}

/// <summary>
/// DTO for daemon session information
/// </summary>
public class DaemonSessionDto
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string AuthState { get; set; } = string.Empty;
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int TimeRemainingSeconds { get; set; }

    public static DaemonSessionDto FromSession(DaemonSession session)
    {
        return new DaemonSessionDto
        {
            Id = session.Id,
            UserId = session.UserId,
            ContainerName = session.ContainerName,
            Status = session.Status.ToString(),
            AuthState = session.AuthState.ToString(),
            IsPrefilling = session.IsPrefilling,
            CreatedAt = session.CreatedAt,
            EndedAt = session.EndedAt,
            ExpiresAt = session.ExpiresAt,
            TimeRemainingSeconds = Math.Max(0, (int)(session.ExpiresAt - DateTime.UtcNow).TotalSeconds)
        };
    }
}
