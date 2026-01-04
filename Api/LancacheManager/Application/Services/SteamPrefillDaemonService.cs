using System.Collections.Concurrent;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Application.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
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
    private readonly IHubContext<DownloadHub> _downloadHubContext;
    private readonly IConfiguration _configuration;
    private readonly PrefillSessionService _sessionService;
    private readonly ConcurrentDictionary<string, DaemonSession> _sessions = new();
    private DockerClient? _dockerClient;
    private Timer? _cleanupTimer;
    private bool _disposed;

    // Configuration defaults
    private const int DefaultSessionTimeoutMinutes = 120;
    private const string DefaultDockerImage = "ghcr.io/regix1/steam-prefill-daemon:latest";

    /// <summary>
    /// Indicates whether Docker is available and connected.
    /// </summary>
    public bool IsDockerAvailable => _dockerClient != null;

    public SteamPrefillDaemonService(
        ILogger<SteamPrefillDaemonService> logger,
        IHubContext<PrefillDaemonHub> hubContext,
        IHubContext<DownloadHub> downloadHubContext,
        IConfiguration configuration,
        PrefillSessionService sessionService)
    {
        _logger = logger;
        _hubContext = hubContext;
        _downloadHubContext = downloadHubContext;
        _configuration = configuration;
        _sessionService = sessionService;
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
                // Log clean message without stack trace - Docker not running is expected in many setups
                _logger.LogWarning("Docker is not available - Steam Prefill feature will be disabled. Start Docker Desktop to enable it.");
                _logger.LogTrace(ex, "Docker connection error details");
                _dockerClient = null;
            }

            // Ensure image is available
            if (_dockerClient != null)
            {
                await EnsureImageExistsAsync(cancellationToken);

                // Cleanup orphaned containers from previous runs
                await CleanupOrphanedContainersAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log clean message without stack trace
            _logger.LogWarning("Failed to initialize Docker client - Steam Prefill feature will be disabled.");
            _logger.LogTrace(ex, "Docker initialization error details");
            _dockerClient = null;
        }

        // Start cleanup timer (every minute)
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        _logger.LogInformation("SteamPrefillDaemonService started. Docker available: {DockerAvailable}", _dockerClient != null);
    }

    /// <summary>
    /// Cleans up orphaned prefill daemon containers from previous app runs.
    /// Looks for containers matching the prefill-daemon-* naming pattern.
    /// </summary>
    private async Task CleanupOrphanedContainersAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        try
        {
            // Mark any "Active" sessions in DB as orphaned
            var orphanedSessions = await _sessionService.MarkOrphanedSessionsAsync();

            // Find all running prefill daemon containers
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool>
                        {
                            ["prefill-daemon-"] = true
                        }
                    }
                },
                cancellationToken);

            if (containers.Count == 0)
            {
                _logger.LogInformation("No orphaned prefill daemon containers found");
                return;
            }

            _logger.LogWarning("Found {Count} orphaned prefill daemon containers to cleanup", containers.Count);

            foreach (var container in containers)
            {
                try
                {
                    // Stop and remove the container
                    if (container.State == "running")
                    {
                        await _dockerClient.Containers.StopContainerAsync(
                            container.ID,
                            new ContainerStopParameters { WaitBeforeKillSeconds = 1 },
                            cancellationToken);
                    }

                    await _dockerClient.Containers.RemoveContainerAsync(
                        container.ID,
                        new ContainerRemoveParameters { Force = true },
                        cancellationToken);

                    _logger.LogInformation("Cleaned up orphaned container: {Name} ({Id})",
                        container.Names.FirstOrDefault() ?? "unknown",
                        container.ID[..12]);

                    // Mark as cleaned in database
                    await _sessionService.MarkOrphanedSessionCleanedAsync(container.ID);
                }
                catch (DockerApiException ex) when (ex.Message.Contains("removal") && ex.Message.Contains("already in progress"))
                {
                    // Another cleanup operation is already removing this container - that's fine
                    _logger.LogDebug("Container {Id} removal already in progress, skipping", container.ID[..12]);
                }
                catch (DockerContainerNotFoundException)
                {
                    // Container was already removed (AutoRemove or concurrent cleanup)
                    _logger.LogDebug("Container {Id} already removed", container.ID[..12]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup orphaned container {Id}", container.ID[..12]);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during orphaned container cleanup");
        }
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
    public async Task<DaemonSession> CreateSessionAsync(
        string userId,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (_dockerClient == null)
        {
            throw new InvalidOperationException(
                "Docker is not running or not accessible. Please start Docker Desktop and try again.");
        }

        // Check if user already has an active session - return it instead of creating a new one
        var existingSession = _sessions.Values.FirstOrDefault(s => s.UserId == userId && s.Status == DaemonSessionStatus.Active);
        if (existingSession != null)
        {
            _logger.LogInformation("Returning existing active session {SessionId} for user {UserId}", existingSession.Id, userId);
            return existingSession;
        }

        // Always pull latest image before creating session
        await EnsureImageExistsAsync(cancellationToken);

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

        // Parse user agent for OS and browser info
        var (os, browser) = UserAgentParser.Parse(userAgent);

        var session = new DaemonSession
        {
            Id = sessionId,
            UserId = userId,
            ContainerId = containerId,
            ContainerName = containerName,
            CommandsDir = commandsDir,
            ResponsesDir = responsesDir,
            ExpiresAt = DateTime.UtcNow.AddMinutes(GetSessionTimeoutMinutes()),
            IpAddress = ipAddress,
            UserAgent = userAgent,
            OperatingSystem = os,
            Browser = browser,
            LastSeenAt = DateTime.UtcNow
        };

        // Create daemon client
        session.Client = new DaemonClient(commandsDir, responsesDir);

        // Start watching for status and challenge files
        StartStatusWatcher(session);

        _sessions[sessionId] = session;

        // Persist session to database for admin visibility and orphan tracking
        await _sessionService.CreateSessionAsync(
            sessionId,
            userId,
            containerId,
            containerName,
            session.ExpiresAt);

        _logger.LogInformation("Created daemon session {SessionId} for user {UserId}", sessionId, userId);

        // Broadcast session creation to all clients for real-time updates (both hubs)
        var sessionDto = DaemonSessionDto.FromSession(session);
        await _hubContext.Clients.All.SendAsync("DaemonSessionCreated", sessionDto);
        await _downloadHubContext.Clients.All.SendAsync("DaemonSessionCreated", sessionDto);

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

        // If this is the username credential, capture it and check for bans
        if (challenge.CredentialType.Equals("username", StringComparison.OrdinalIgnoreCase))
        {
            session.SteamUsername = credential;

            // Check if this user is banned
            if (await _sessionService.IsUsernameBannedAsync(credential))
            {
                _logger.LogWarning("Blocked banned Steam user {Username} from logging in. Session: {SessionId}",
                    credential, sessionId);

                // Clean up the pending challenge so the next login attempt starts fresh
                session.Client.ClearPendingChallenges();

                // Reset auth state to allow for a clean error display
                session.AuthState = DaemonAuthState.NotAuthenticated;
                await NotifyAuthStateChangeAsync(session);

                throw new UnauthorizedAccessException("This Steam account has been banned from using prefill.");
            }

            // Update the database record with the username
            await _sessionService.SetSessionUsernameAsync(sessionId, credential);

            _logger.LogInformation("Captured Steam username for session {SessionId}: {Username}",
                sessionId, credential);

            // Broadcast session update to all clients for real-time updates (both hubs)
            var updatedDto = DaemonSessionDto.FromSession(session);
            await _hubContext.Clients.All.SendAsync("DaemonSessionUpdated", updatedDto);
            await _downloadHubContext.Clients.All.SendAsync("DaemonSessionUpdated", updatedDto);
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
    /// Cancels a pending login attempt and resets auth state.
    /// Sends cancel-login command to the daemon to abort any pending credential waits.
    /// </summary>
    public async Task CancelLoginAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        _logger.LogInformation("Cancelling login for session {SessionId}", sessionId);

        try
        {
            // Send cancel-login command to daemon - this will abort any pending credential waits
            await session.Client.CancelLoginAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending cancel-login to daemon for session {SessionId}", sessionId);
            // Continue with local cleanup even if daemon command fails
        }

        // Also clear any pending challenge files on our side
        session.Client.ClearPendingChallenges();

        // Reset auth state to allow a new login attempt
        session.AuthState = DaemonAuthState.NotAuthenticated;
        await NotifyAuthStateChangeAsync(session);

        _logger.LogInformation("Login cancelled for session {SessionId}, ready for new attempt", sessionId);
    }

    /// <summary>
    /// Cancels a running prefill operation.
    /// Sends cancel-prefill command to the daemon to abort the download.
    /// </summary>
    public async Task CancelPrefillAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        _logger.LogInformation("Cancelling prefill for session {SessionId}", sessionId);

        try
        {
            await session.Client.CancelPrefillAsync(cancellationToken);

            // Cancel any in-progress history entries
            await _sessionService.CancelPrefillEntriesAsync(sessionId);

            // Broadcast history update if there was a current app
            if (session.CurrentAppId > 0)
            {
                await BroadcastPrefillHistoryUpdatedAsync(sessionId, session.CurrentAppId, "Cancelled");
            }

            _logger.LogInformation("Prefill cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending cancel-prefill to daemon for session {SessionId}", sessionId);
            throw;
        }
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
        bool recentlyPurchased = false,
        int? top = null,
        bool force = false,
        List<string>? operatingSystems = null,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        session.IsPrefilling = true;
        session.PreviousAppId = 0;
        session.PreviousAppName = null;
        await NotifyPrefillStateChangeAsync(session, "started");
        var startDto = DaemonSessionDto.FromSession(session);
        await _hubContext.Clients.All.SendAsync("DaemonSessionUpdated", startDto);
        await _downloadHubContext.Clients.All.SendAsync("DaemonSessionUpdated", startDto);

        try
        {
            var result = await session.Client.PrefillAsync(all, recent, recentlyPurchased, top, force, operatingSystems, cancellationToken);
            await NotifyPrefillStateChangeAsync(session, result.Success ? "completed" : "failed");

            // Complete the last in-progress entry if any (the final game)
            if (session.CurrentAppId > 0)
            {
                try
                {
                    await _sessionService.CompletePrefillEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        result.Success ? "Completed" : "Failed",
                        0, 0, // bytes will be in the result
                        result.Success ? null : "Prefill ended");

                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, session.CurrentAppId,
                        result.Success ? "Completed" : "Failed");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete final prefill history entry");
                }
            }

            return result;
        }
        finally
        {
            session.IsPrefilling = false;
            session.CurrentAppId = 0;
            session.CurrentAppName = null;
            session.PreviousAppId = 0;
            session.PreviousAppName = null;
            var endDto = DaemonSessionDto.FromSession(session);
            await _hubContext.Clients.All.SendAsync("DaemonSessionUpdated", endDto);
            await _downloadHubContext.Clients.All.SendAsync("DaemonSessionUpdated", endDto);
        }
    }

    /// <summary>
    /// Clears the temporary cache
    /// </summary>
    public async Task<ClearCacheResult> ClearCacheAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.ClearCacheAsync(cancellationToken);
    }

    /// <summary>
    /// Gets cache info
    /// </summary>
    public async Task<ClearCacheResult> GetCacheInfoAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.GetCacheInfoAsync(cancellationToken);
    }

    /// <summary>
    /// Gets selected apps status with download sizes
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.GetSelectedAppsStatusAsync(cancellationToken);
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
    /// <param name="force">If true, kills the container immediately without graceful shutdown</param>
    public async Task TerminateSessionAsync(string sessionId, string reason = "User requested", bool force = false, string? terminatedBy = null)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        _logger.LogInformation("Terminating session {SessionId}: {Reason} (force={Force})", sessionId, reason, force);

        // Cancel any in-progress history entries
        await _sessionService.CancelPrefillEntriesAsync(sessionId);

        // Persist termination to database
        await _sessionService.TerminateSessionAsync(sessionId, reason, terminatedBy);

        session.Status = DaemonSessionStatus.Terminated;
        session.EndedAt = DateTime.UtcNow;

        // Broadcast session termination to all clients for real-time updates (both hubs)
        var terminatedEvent = new { sessionId = session.Id, reason = reason };
        await _hubContext.Clients.All.SendAsync("DaemonSessionTerminated", terminatedEvent);
        await _downloadHubContext.Clients.All.SendAsync("DaemonSessionTerminated", terminatedEvent);

        // Cancel any ongoing operations immediately
        session.CancellationTokenSource.Cancel();

        // Kill container immediately if force, otherwise try graceful shutdown
        if (_dockerClient != null && !string.IsNullOrEmpty(session.ContainerId))
        {
            try
            {
                if (force)
                {
                    // Kill immediately without waiting
                    await _dockerClient.Containers.KillContainerAsync(
                        session.ContainerId,
                        new ContainerKillParameters());
                    _logger.LogInformation("Force killed container {ContainerId} for session {SessionId}",
                        session.ContainerId, sessionId);
                }
                else
                {
                    // Try graceful shutdown first (with very short timeout)
                    try
                    {
                        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await session.Client.ShutdownAsync(shutdownCts.Token);
                    }
                    catch
                    {
                        // Ignore shutdown errors - container will be killed
                    }

                    // Stop with minimal wait time
                    await _dockerClient.Containers.StopContainerAsync(
                        session.ContainerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 1 });

                    _logger.LogInformation("Stopped container {ContainerId} for session {SessionId}",
                        session.ContainerId, sessionId);
                }
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
        _logger.LogInformation("Pulling latest prefill daemon image: {ImageName}", imageName);

        try
        {
            // Always pull to ensure we have the latest version
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
                        // Only log significant progress, not every layer
                        if (msg.Status.Contains("Pulling") || msg.Status.Contains("Downloaded") || msg.Status.Contains("up to date"))
                        {
                            _logger.LogInformation("Pull: {Status}", msg.Status);
                        }
                    }
                    if (!string.IsNullOrEmpty(msg.ErrorMessage))
                    {
                        _logger.LogError("Pull error: {Error}", msg.ErrorMessage);
                    }
                }),
                cancellationToken);

            var imageInfo = await _dockerClient.Images.InspectImageAsync(imageName, cancellationToken);
            _logger.LogInformation("Image ready: {ImageName} (ID: {ImageId})", imageName, imageInfo.ID[..12]);
        }
        catch (Exception ex)
        {
            // Check if we have a local copy we can use
            try
            {
                var imageInfo = await _dockerClient.Images.InspectImageAsync(imageName, cancellationToken);
                _logger.LogWarning(ex, "Failed to pull latest image, using cached version: {ImageId}", imageInfo.ID[..12]);
            }
            catch (DockerImageNotFoundException)
            {
                _logger.LogError(ex, "Failed to pull image {ImageName} and no cached version available. " +
                    "The Steam Prefill feature requires this image. " +
                    "Ensure the image exists at the registry or build it from: https://github.com/regix1/steam-prefill-daemon",
                    imageName);
                throw;
            }
        }
    }

    private void StartStatusWatcher(DaemonSession session)
    {
        // Use a ConcurrentDictionary to track pending file changes for deduplication
        // FileSystemWatcher fires multiple events for a single file change
        var pendingChanges = new ConcurrentDictionary<string, DateTime>();
        var processLock = new SemaphoreSlim(1, 1);

        session.StatusWatcher = new FileSystemWatcher(session.ResponsesDir, "*.json")
        {
            // Only watch for LastWrite - this reduces duplicate events
            NotifyFilter = NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        // Use a single handler for all changes to simplify deduplication
        async void HandleFileChange(object sender, FileSystemEventArgs e)
        {
            var fileName = Path.GetFileName(e.FullPath);
            var now = DateTime.UtcNow;

            // Deduplicate: if we've processed this file in the last 100ms, skip it
            if (pendingChanges.TryGetValue(fileName, out var lastProcessed) &&
                (now - lastProcessed).TotalMilliseconds < 100)
            {
                return;
            }

            pendingChanges[fileName] = now;

            // Wait a bit for file to be fully written
            await Task.Delay(50);

            // Use lock to ensure we don't process the same file concurrently
            await processLock.WaitAsync();
            try
            {
                if (fileName.StartsWith("auth_challenge_"))
                {
                    await HandleResponseFileAsync(session, e.FullPath);
                }
                else if (fileName == "daemon_status.json")
                {
                    await HandleStatusChangeAsync(session, e.FullPath);
                }
                else if (fileName == "prefill_progress.json")
                {
                    await HandleProgressChangeAsync(session, e.FullPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error handling file change {Path}", e.FullPath);
            }
            finally
            {
                processLock.Release();
            }
        }

        session.StatusWatcher.Changed += HandleFileChange;
        session.StatusWatcher.Created += HandleFileChange;
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
            
            // Note: PropertyNameCaseInsensitive handles camelCase/PascalCase
            var progress = JsonSerializer.Deserialize<PrefillProgress>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (progress != null)
            {
                _logger.LogDebug("Parsed progress - AppId: {AppId}, AppName: {AppName}, BytesDownloaded: {Bytes}, TotalBytes: {Total}",
                    progress.CurrentAppId, progress.CurrentAppName, progress.BytesDownloaded, progress.TotalBytes);

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
        // Update session's current app info for admin visibility
        var appInfoChanged = session.CurrentAppId != progress.CurrentAppId ||
                             session.CurrentAppName != progress.CurrentAppName;

        // Track history: detect game transitions
        if (appInfoChanged && progress.CurrentAppId > 0)
        {
            // If there was an app being prefilled, complete its history entry
            // Use the STORED bytes (from before the transition), not progress bytes (which are for the new app)
            if (session.CurrentAppId > 0)
            {
                try
                {
                    // Complete the current app's entry using stored values (this is the app that just finished)
                    await _sessionService.CompletePrefillEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        "Completed",
                        session.CurrentBytesDownloaded,  // Use stored bytes, not progress.BytesDownloaded
                        session.CurrentTotalBytes);      // Use stored total, not progress.TotalBytes

                    _logger.LogInformation("Completed prefill history for app {AppId} ({AppName}) in session {SessionId}: {Bytes}/{Total} bytes",
                        session.CurrentAppId, session.CurrentAppName, session.Id,
                        session.CurrentBytesDownloaded, session.CurrentTotalBytes);

                    // Broadcast history update
                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, session.CurrentAppId, "Completed");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history entry for app {AppId}", session.CurrentAppId);
                }
            }

            // Start a new history entry for the current app
            try
            {
                await _sessionService.StartPrefillEntryAsync(session.Id, progress.CurrentAppId, progress.CurrentAppName);

                _logger.LogDebug("Started prefill history for app {AppId} ({AppName}) in session {SessionId}",
                    progress.CurrentAppId, progress.CurrentAppName, session.Id);

                // Broadcast history update
                await BroadcastPrefillHistoryUpdatedAsync(session.Id, progress.CurrentAppId, "InProgress");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start prefill history entry for app {AppId}", progress.CurrentAppId);
            }

            // Reset bytes tracking for the new app
            session.CurrentBytesDownloaded = 0;
            session.CurrentTotalBytes = 0;
        }

        // Handle completion/failure states
        if (progress.State == "completed" || progress.State == "failed" || progress.State == "error")
        {
            if (session.CurrentAppId > 0)
            {
                try
                {
                    var status = progress.State == "completed" ? "Completed" : "Failed";
                    // For completion states, use the current progress values (they're for the current app)
                    await _sessionService.CompletePrefillEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        status,
                        progress.BytesDownloaded,
                        progress.TotalBytes,
                        progress.ErrorMessage);

                    _logger.LogDebug("Completed prefill history for app {AppId} ({AppName}) with status {Status}",
                        session.CurrentAppId, session.CurrentAppName, status);

                    // Broadcast history update
                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, session.CurrentAppId, status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history entry for app {AppId}", session.CurrentAppId);
                }
            }
        }

        // Track current app's bytes BEFORE updating app info (so we have final values for next transition)
        if (!appInfoChanged && progress.CurrentAppId > 0)
        {
            session.CurrentBytesDownloaded = progress.BytesDownloaded;
            session.CurrentTotalBytes = progress.TotalBytes;
        }

        // Update previous app tracking before changing current
        session.PreviousAppId = session.CurrentAppId;
        session.PreviousAppName = session.CurrentAppName;
        session.CurrentAppId = progress.CurrentAppId;
        session.CurrentAppName = progress.CurrentAppName;
        
        // Calculate total bytes transferred ourselves since daemon doesn't track it
        // Use progress.TotalBytesTransferred if available, otherwise calculate from bytesDownloaded
        if (progress.TotalBytesTransferred > 0)
        {
            session.TotalBytesTransferred = progress.TotalBytesTransferred;
        }
        else
        {
            // When transitioning to a new app, add the completed app's bytes to the running total
            if (appInfoChanged && session.CurrentBytesDownloaded > 0)
            {
                session.CompletedBytesTransferred += session.CurrentBytesDownloaded;
            }
            // Total = completed games + current game progress (for real-time display)
            session.TotalBytesTransferred = session.CompletedBytesTransferred + progress.BytesDownloaded;
        }

        // Broadcast session update to all clients when app info changes (for admin pages - both hubs)
        if (appInfoChanged)
        {
            var progressDto = DaemonSessionDto.FromSession(session);
            await _hubContext.Clients.All.SendAsync("DaemonSessionUpdated", progressDto);
            await _downloadHubContext.Clients.All.SendAsync("DaemonSessionUpdated", progressDto);
        }

        // Send detailed progress to subscribed connections (the user doing the prefill)
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

    private async Task BroadcastPrefillHistoryUpdatedAsync(string sessionId, uint appId, string status)
    {
        var historyEvent = new { sessionId, appId, status };
        await _hubContext.Clients.All.SendAsync("PrefillHistoryUpdated", historyEvent);
        await _downloadHubContext.Clients.All.SendAsync("PrefillHistoryUpdated", historyEvent);
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

    /// <summary>
    /// The Steam username (set when user provides username credential).
    /// Used for ban display and admin visibility.
    /// </summary>
    public string? SteamUsername { get; set; }

    /// <summary>
    /// Current prefill progress info for admin visibility
    /// </summary>
    public uint CurrentAppId { get; set; }
    public string? CurrentAppName { get; set; }

    /// <summary>
    /// Previous app ID for tracking history transitions
    /// </summary>
    public uint PreviousAppId { get; set; }
    public string? PreviousAppName { get; set; }

    /// <summary>
    /// Total bytes transferred during this session (cumulative across all games)
    /// This includes completed games + current game progress for real-time display
    /// </summary>
    public long TotalBytesTransferred { get; set; }
    
    /// <summary>
    /// Bytes from completed games (used to calculate TotalBytesTransferred)
    /// </summary>
    public long CompletedBytesTransferred { get; set; }

    /// <summary>
    /// Current app's bytes downloaded (tracked before transition to record final values)
    /// </summary>
    public long CurrentBytesDownloaded { get; set; }
    public long CurrentTotalBytes { get; set; }

    /// <summary>
    /// Client connection info for admin visibility
    /// </summary>
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? OperatingSystem { get; init; }
    public string? Browser { get; init; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

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

    // Client info for admin visibility
    public string? IpAddress { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Browser { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? SteamUsername { get; set; }

    // Current prefill progress info for admin visibility
    public uint CurrentAppId { get; set; }
    public string? CurrentAppName { get; set; }
    
    /// <summary>
    /// Total bytes transferred during this session (cumulative across all games)
    /// </summary>
    public long TotalBytesTransferred { get; set; }

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
            TimeRemainingSeconds = Math.Max(0, (int)(session.ExpiresAt - DateTime.UtcNow).TotalSeconds),
            IpAddress = session.IpAddress,
            OperatingSystem = session.OperatingSystem,
            Browser = session.Browser,
            LastSeenAt = session.LastSeenAt,
            SteamUsername = session.SteamUsername,
            CurrentAppId = session.CurrentAppId,
            CurrentAppName = session.CurrentAppName,
            TotalBytesTransferred = session.TotalBytesTransferred
        };
    }
}

/// <summary>
/// Prefill progress update from the daemon
/// </summary>
public class PrefillProgress
{
    public string State { get; set; } = "idle";
    public string? Message { get; set; }
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
