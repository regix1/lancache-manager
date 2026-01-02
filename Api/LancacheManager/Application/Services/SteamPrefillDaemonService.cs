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

            // Check if Docker socket exists
            if (!OperatingSystem.IsWindows() && !File.Exists("/var/run/docker.sock"))
            {
                _logger.LogWarning("Docker socket not found at /var/run/docker.sock. " +
                    "Mount the Docker socket to enable prefill containers: -v /var/run/docker.sock:/var/run/docker.sock");
            }

            _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();

            // Test connection
            try
            {
                var version = await _dockerClient.System.GetVersionAsync(cancellationToken);
                _logger.LogInformation("Docker client connected. Docker version: {Version}", version.Version);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Docker client created but cannot connect. Ensure Docker socket is mounted.");
                _dockerClient = null;
            }

            // Ensure image is available
            if (_dockerClient != null)
            {
                await EnsureImageExistsAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Docker client. Container management will be unavailable.");
            _dockerClient = null;
        }

        // Start cleanup timer (every minute)
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        _logger.LogInformation("SteamPrefillDaemonService started. Docker available: {DockerAvailable}", _dockerClient != null);
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

        // Create directories inside this container
        Directory.CreateDirectory(commandsDir);
        Directory.CreateDirectory(responsesDir);

        // For Docker bind mounts, we need to translate container paths to host paths
        // /data inside this container maps to the host's data directory
        var hostDataPath = await GetHostDataPathAsync(cancellationToken);
        var hostCommandsDir = commandsDir.Replace("/data", hostDataPath);
        var hostResponsesDir = responsesDir.Replace("/data", hostDataPath);

        _logger.LogInformation("Creating daemon container for session {SessionId}, user {UserId}", sessionId, userId);
        _logger.LogDebug("Container paths: commands={CommandsDir}, responses={ResponsesDir}", commandsDir, responsesDir);
        _logger.LogDebug("Host paths: commands={HostCommandsDir}, responses={HostResponsesDir}", hostCommandsDir, hostResponsesDir);

        // Create and start container
        var containerName = $"prefill-daemon-{sessionId}";
        var imageName = GetImageName();

        var createResponse = await _dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Name = containerName,
                Image = imageName,
                // Run in daemon mode - watches for command files
                Cmd = new List<string> { "daemon", "-c", "/commands", "-r", "/responses" },
                Env = new List<string>
                {
                    $"PREFILL_COMMANDS_DIR=/commands",
                    $"PREFILL_RESPONSES_DIR=/responses"
                },
                HostConfig = new HostConfig
                {
                    Binds = new List<string>
                    {
                        $"{hostCommandsDir}:/commands",
                        $"{hostResponsesDir}:/responses"
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

        // Verify container is actually running (it may have crashed immediately)
        await Task.Delay(1000, cancellationToken); // Give it a moment to crash if it's going to
        try
        {
            var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);
            if (!inspect.State.Running)
            {
                var exitCode = inspect.State.ExitCode;
                var error = inspect.State.Error;
                _logger.LogError("Container {ContainerId} exited immediately! ExitCode: {ExitCode}, Error: {Error}",
                    containerId, exitCode, error);

                // Try to get logs
                try
                {
                    var logParams = new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "50" };
                    using var logStream = await _dockerClient.Containers.GetContainerLogsAsync(containerId, false, logParams, cancellationToken);
                    using var memoryStream = new MemoryStream();
                    await logStream.CopyOutputToAsync(null, memoryStream, null, cancellationToken);
                    memoryStream.Position = 0;
                    using var reader = new StreamReader(memoryStream);
                    var logs = await reader.ReadToEndAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(logs))
                    {
                        _logger.LogError("Container logs:\n{Logs}", logs);
                    }
                    else
                    {
                        _logger.LogError("Container produced no logs before exiting");
                    }
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "Could not retrieve container logs");
                }

                throw new InvalidOperationException($"Container crashed on startup. ExitCode: {exitCode}. Check if image '{imageName}' exists and is valid.");
            }

            _logger.LogInformation("Container {ContainerId} verified running for session {SessionId}", containerId, sessionId);
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogError("Container {ContainerId} was removed before we could verify it (crashed immediately with AutoRemove). " +
                "Image '{ImageName}' may not exist or is crashing on startup.", containerId, imageName);
            throw new InvalidOperationException($"Container crashed immediately. Ensure image '{imageName}' exists and is properly configured.");
        }

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

        _logger.LogInformation("Starting login for session {SessionId}. ResponsesDir: {ResponsesDir}",
            sessionId, session.ResponsesDir);

        // Log what files exist in the responses directory
        if (Directory.Exists(session.ResponsesDir))
        {
            var files = Directory.GetFiles(session.ResponsesDir);
            _logger.LogInformation("Files in responses dir before login: {Files}",
                files.Length > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : "(empty)");
        }
        else
        {
            _logger.LogWarning("Responses directory does not exist: {ResponsesDir}", session.ResponsesDir);
        }

        session.AuthState = DaemonAuthState.LoggingIn;
        await NotifyAuthStateChangeAsync(session);

        var challenge = await session.Client.StartLoginAsync(timeout, cancellationToken);

        // Log result
        if (challenge != null)
        {
            _logger.LogInformation("Received challenge for session {SessionId}: Type={Type}, Id={ChallengeId}",
                sessionId, challenge.CredentialType, challenge.ChallengeId);
        }
        else
        {
            // Log what files exist now
            if (Directory.Exists(session.ResponsesDir))
            {
                var files = Directory.GetFiles(session.ResponsesDir);
                _logger.LogWarning("No challenge received. Files in responses dir: {Files}",
                    files.Length > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : "(empty)");
            }
        }

        return challenge;
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

        _logger.LogInformation("SetSelectedAppsAsync: Sending {Count} app IDs to daemon for session {SessionId}", appIds.Count, sessionId);

        await session.Client.SetSelectedAppsAsync(appIds, cancellationToken);

        _logger.LogInformation("SetSelectedAppsAsync: Daemon acknowledged for session {SessionId}", sessionId);
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
        _logger.LogInformation("Checking for prefill daemon image: {ImageName}", imageName);

        try
        {
            var imageInfo = await _dockerClient.Images.InspectImageAsync(imageName, cancellationToken);
            _logger.LogInformation("Image exists: {ImageName} (ID: {ImageId})", imageName, imageInfo.ID[..12]);
        }
        catch (DockerImageNotFoundException)
        {
            _logger.LogInformation("Image not found locally, pulling: {ImageName}", imageName);

            try
            {
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
                            var progress = msg.Progress?.Current > 0 ? $"{msg.Progress.Current}/{msg.Progress.Total}" : "";
                            _logger.LogInformation("Pull progress: {Status} {Progress}", msg.Status, progress);
                        }
                        if (!string.IsNullOrEmpty(msg.ErrorMessage))
                        {
                            _logger.LogError("Pull error: {Error}", msg.ErrorMessage);
                        }
                    }),
                    cancellationToken);

                _logger.LogInformation("Image pulled successfully: {ImageName}", imageName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to pull image {ImageName}. The Steam Prefill feature requires this image. " +
                    "Ensure the image exists at the registry or build it from: https://github.com/regix1/steam-prefill-daemon",
                    imageName);
                throw;
            }
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
                var fileName = Path.GetFileName(e.FullPath);

                // Handle daemon_status.json changes
                if (fileName == "daemon_status.json")
                {
                    await HandleStatusChangeAsync(session, e.FullPath);
                }
                // Handle prefill_progress.json changes
                else if (fileName == "prefill_progress.json")
                {
                    await HandleProgressChangeAsync(session, e.FullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling file change {Path}", e.FullPath);
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
                // Check if file still exists (daemon may have deleted it after processing)
                if (!File.Exists(filePath))
                {
                    _logger.LogDebug("Challenge file no longer exists (already processed): {Path}", filePath);
                    return;
                }

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
            catch (FileNotFoundException)
            {
                // File was deleted between existence check and read - this is normal
                _logger.LogDebug("Challenge file was deleted during read (already processed): {Path}", filePath);
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

    private async Task HandleProgressChangeAsync(DaemonSession session, string filePath)
    {
        await Task.Delay(50); // Ensure file is written

        try
        {
            if (!File.Exists(filePath))
            {
                return; // File was deleted (prefill completed)
            }

            var json = await File.ReadAllTextAsync(filePath);
            var progress = JsonSerializer.Deserialize<PrefillProgress>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (progress != null)
            {
                await NotifyPrefillProgressAsync(session, progress);
            }
        }
        catch (FileNotFoundException)
        {
            // File was deleted during read - prefill completed
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing progress from {Path}", filePath);
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

    private async Task NotifyPrefillProgressAsync(DaemonSession session, PrefillProgress progress)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("PrefillProgress", session.Id, progress);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify prefill progress to {ConnectionId}", connectionId);
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
        // Use /data directory which is a host mount, so both containers can access it
        // The lancache-manager container writes here, and the prefill-daemon container reads from here
        return _configuration["Prefill:DaemonBasePath"] ?? "/data/prefill-sessions";
    }

    private string? _cachedHostDataPath;

    private async Task<string> GetHostDataPathAsync(CancellationToken cancellationToken = default)
    {
        // Return cached value if available
        if (_cachedHostDataPath != null)
            return _cachedHostDataPath;

        // Check for explicit configuration first
        var configuredPath = _configuration["Prefill:HostDataPath"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            _cachedHostDataPath = configuredPath;
            return configuredPath;
        }

        // Auto-detect by inspecting our own container's mounts
        if (_dockerClient != null)
        {
            try
            {
                var containerId = GetOwnContainerId();
                if (!string.IsNullOrEmpty(containerId))
                {
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);

                    // Find the mount for /data
                    var dataMount = inspect.Mounts?.FirstOrDefault(m => m.Destination == "/data");
                    if (dataMount != null)
                    {
                        _cachedHostDataPath = dataMount.Source;
                        _logger.LogInformation("Auto-detected host data path: {HostDataPath}", _cachedHostDataPath);
                        return _cachedHostDataPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-detect host data path from container mounts");
            }
        }

        // Fallback - assume running directly on host
        _cachedHostDataPath = "/data";
        _logger.LogWarning("Could not auto-detect host data path, using fallback: {HostDataPath}. " +
            "Set Prefill__HostDataPath environment variable if prefill containers can't access command files.",
            _cachedHostDataPath);
        return _cachedHostDataPath;
    }

    private string? GetOwnContainerId()
    {
        // Try to get container ID from cgroup
        try
        {
            // In Docker, /proc/1/cpuset contains the container ID
            if (File.Exists("/proc/1/cpuset"))
            {
                var cpuset = File.ReadAllText("/proc/1/cpuset").Trim();
                // Format: /docker/<container_id> or /kubepods/.../<container_id>
                var parts = cpuset.Split('/');
                if (parts.Length > 0)
                {
                    var lastPart = parts[^1];
                    if (lastPart.Length >= 12)
                        return lastPart;
                }
            }

            // Try /proc/self/cgroup
            if (File.Exists("/proc/self/cgroup"))
            {
                var cgroup = File.ReadAllText("/proc/self/cgroup");
                foreach (var line in cgroup.Split('\n'))
                {
                    // Format: 0::/docker/<container_id>
                    if (line.Contains("/docker/"))
                    {
                        var idx = line.LastIndexOf("/docker/");
                        if (idx >= 0)
                        {
                            var id = line[(idx + 8)..].Trim();
                            if (id.Length >= 12)
                                return id;
                        }
                    }
                }
            }

            // Try hostname (often set to container ID in Docker)
            var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
            if (!string.IsNullOrEmpty(hostname) && hostname.Length >= 12)
            {
                return hostname;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get own container ID");
        }

        return null;
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

/// <summary>
/// Prefill progress update from the daemon
/// </summary>
public class PrefillProgress
{
    public string State { get; set; } = "idle";
    public uint CurrentAppId { get; set; }
    public string? CurrentAppName { get; set; }
    public long TotalBytes { get; set; }
    public long BytesDownloaded { get; set; }
    public double PercentComplete { get; set; }
    public double BytesPerSecond { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalApps { get; set; }
    public int UpdatedApps { get; set; }
    public int AlreadyUpToDate { get; set; }
    public int FailedApps { get; set; }
    public long TotalBytesTransferred { get; set; }
    public double TotalTimeSeconds { get; set; }
    public DateTime UpdatedAt { get; set; }
}
