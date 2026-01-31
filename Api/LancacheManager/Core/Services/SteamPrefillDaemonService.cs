using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Manages Steam Prefill daemon Docker containers.
/// Each user session gets its own container with dedicated command/response directories.
/// Uses encrypted credential exchange (ECDH + AES-GCM) for secure authentication.
/// </summary>
public partial class SteamPrefillDaemonService : IHostedService, IDisposable
{
    private readonly ILogger<SteamPrefillDaemonService> _logger;
    private readonly ISignalRNotificationService _notifications;
    private readonly IConfiguration _configuration;
    private readonly IPathResolver _pathResolver;
    private readonly PrefillSessionService _sessionService;
    private readonly PrefillCacheService _cacheService;
    private readonly ISteamAuthStorageService _steamAuthStorage;
    private readonly ConcurrentDictionary<string, DaemonSession> _sessions = new();
    private DockerClient? _dockerClient;
    private Timer? _cleanupTimer;
    private bool _disposed;
    private readonly bool _isRunningInContainer;

    // Configuration defaults
    private const int DefaultSessionTimeoutMinutes = 120;
    private const string DefaultDockerImage = "ghcr.io/regix1/steam-prefill-daemon:latest";
    private const int DefaultTcpPort = 45555;

    /// <summary>
    /// Indicates whether Docker is available and connected.
    /// </summary>
    public bool IsDockerAvailable => _dockerClient != null;

    public SteamPrefillDaemonService(
        ILogger<SteamPrefillDaemonService> logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        ISteamAuthStorageService steamAuthStorage)
    {
        _logger = logger;
        _notifications = notifications;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _sessionService = sessionService;
        _cacheService = cacheService;
        _steamAuthStorage = steamAuthStorage;
        _isRunningInContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
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
        var hostCommandsDir = commandsDir;
        var hostResponsesDir = responsesDir;
        var containerDataRoot = _pathResolver.GetDataDirectory();
        if (!string.IsNullOrEmpty(hostDataPath) &&
            _isRunningInContainer &&
            commandsDir.StartsWith(containerDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            hostCommandsDir = commandsDir.Replace(containerDataRoot, hostDataPath, StringComparison.OrdinalIgnoreCase);
            hostResponsesDir = responsesDir.Replace(containerDataRoot, hostDataPath, StringComparison.OrdinalIgnoreCase);
        }

        _logger.LogInformation("Creating daemon container for session {SessionId}, user {UserId}", sessionId, userId);
        _logger.LogDebug("Container paths: commands={CommandsDir}, responses={ResponsesDir}", commandsDir, responsesDir);
        _logger.LogDebug("Host paths: commands={HostCommandsDir}, responses={HostResponsesDir}", hostCommandsDir, hostResponsesDir);

        // Create and start container
        var containerName = $"prefill-daemon-{sessionId}";
        var imageName = GetImageName();

        // Get network configuration for prefill container
        // Auto-detect from lancache-dns container if not explicitly configured
        var useHostNetworking = await ShouldUseHostNetworkingAsync(cancellationToken);
        var lancacheDnsIp = useHostNetworking ? null : await GetLancacheDnsIpAsync(cancellationToken);
        var explicitNetworkMode = GetNetworkMode();

        // Build host config with proper network settings
        var hostConfig = new HostConfig
        {
            Binds = new List<string>
            {
                $"{hostCommandsDir}:/commands",
                $"{hostResponsesDir}:/responses"
            },
            AutoRemove = true
        };

        // Disable IPv6 to ensure DNS queries go through IPv4 lancache-dns
        // This prevents IPv6 DNS bypass which can cause prefill to miss the cache
        // Note: Sysctls are not allowed with host networking mode
        var shouldDisableIpv6 = !useHostNetworking &&
                                (string.IsNullOrEmpty(explicitNetworkMode) ||
                                 !explicitNetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase));
        if (shouldDisableIpv6)
        {
            hostConfig.Sysctls = new Dictionary<string, string>
            {
                ["net.ipv6.conf.all.disable_ipv6"] = "1"
            };
        }

        // Determine network configuration strategy:
        // 1. If explicitly configured with NetworkMode, use that
        // 2. If lancache-dns uses host networking, use host mode (auto-detected)
        // 3. Otherwise use default networking
        // DNS is always configured when available (except for host mode which inherits host DNS)
        if (!string.IsNullOrEmpty(explicitNetworkMode))
        {
            hostConfig.NetworkMode = explicitNetworkMode;
            _logger.LogInformation("Configuring prefill container network mode (explicit): {NetworkMode}", explicitNetworkMode);
        }
        else if (useHostNetworking)
        {
            hostConfig.NetworkMode = "host";
            _logger.LogInformation("Configuring prefill container to use host networking (auto-detected from lancache-dns)");
        }

        // Configure DNS for non-host network modes
        var isHostMode = useHostNetworking || 
                         (!string.IsNullOrEmpty(explicitNetworkMode) && 
                          explicitNetworkMode.Equals("host", StringComparison.OrdinalIgnoreCase));
        
        if (!isHostMode && !string.IsNullOrEmpty(lancacheDnsIp))
        {
            hostConfig.DNS = new List<string> { lancacheDnsIp };
            _logger.LogInformation("Configuring prefill container DNS to use lancache-dns: {DnsIp}", lancacheDnsIp);
        }
        else if (!isHostMode && string.IsNullOrEmpty(lancacheDnsIp))
        {
            _logger.LogWarning("Could not auto-detect lancache-dns configuration. Prefill may fail if the host's DNS " +
                "doesn't resolve Steam CDN to lancache. Set Prefill__LancacheDnsIp or Prefill__NetworkMode=host.");
        }

        // Socket path for Unix Domain Socket communication
        var socketPath = Path.Combine(responsesDir, "daemon.sock");
        var useTcpMode = ShouldUseTcpMode();
        var tcpContainerPort = useTcpMode ? GetContainerTcpPort() : (int?)null;
        var tcpHostPort = useTcpMode ? GetHostTcpPort() : (int?)null;

        // Build command and environment for daemon mode
        var cmd = new List<string> { "daemon" };

        var env = new List<string>
        {
            $"PREFILL_COMMANDS_DIR=/commands",
            $"PREFILL_RESPONSES_DIR=/responses"
        };

        var socketSecret = GenerateSocketSecret();
        env.Add($"PREFILL_SOCKET_SECRET={socketSecret}");
        _logger.LogInformation("Passing generated socket secret to prefill daemon container");

        if (useTcpMode && tcpContainerPort.HasValue)
        {
            env.Add($"PREFILL_TCP_PORT={tcpContainerPort.Value}");
            _logger.LogInformation("Creating daemon container for session {SessionId} using TCP mode (host port {HostPort})",
                sessionId, tcpHostPort);
        }
        else
        {
            env.Add("PREFILL_USE_SOCKET=true");
            env.Add("PREFILL_SOCKET_PATH=/responses/daemon.sock");
            _logger.LogInformation("Creating daemon container for session {SessionId} using socket mode", sessionId);
        }

        if (useTcpMode && tcpContainerPort.HasValue && tcpHostPort.HasValue)
        {
            hostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                [$"{tcpContainerPort.Value}/tcp"] = new List<PortBinding>
                {
                    new() { HostPort = tcpHostPort.Value.ToString(), HostIP = "127.0.0.1" }
                }
            };
        }

        var createResponse = await _dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Name = containerName,
                Image = imageName,
                Cmd = cmd,
                Env = env,
                HostConfig = hostConfig,
                ExposedPorts = useTcpMode && tcpContainerPort.HasValue
                    ? new Dictionary<string, EmptyStruct> { [$"{tcpContainerPort.Value}/tcp"] = default }
                    : null
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

        // Run container network diagnostics (internet connectivity and DNS resolution)
        // This helps troubleshoot prefill issues - the container needs both:
        // 1. Internet access to reach Steam
        // 2. DNS resolving lancache domains to your cache server
        var networkDiagnostics = await TestContainerConnectivityAsync(containerId, containerName, isHostMode, cancellationToken);

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
            LastSeenAt = DateTime.UtcNow,
            NetworkDiagnostics = networkDiagnostics,
            SocketPath = useTcpMode ? null : socketPath
        };

        // Create daemon client
        IDaemonClient daemonClient = useTcpMode && tcpHostPort.HasValue
            ? new TcpDaemonClient(GetTcpHost(), tcpHostPort.Value, socketSecret, _logger as ILogger<TcpDaemonClient>)
            : new SocketDaemonClient(socketPath, socketSecret, _logger as ILogger<SocketDaemonClient>);

        // Wire up socket events to session handlers
        daemonClient.OnCredentialChallenge += async challenge =>
        {
            await HandleSocketCredentialChallengeAsync(session, challenge);
        };
        daemonClient.OnStatusUpdate += async status =>
        {
            await HandleStatusChangeFromSocketAsync(session, status);
        };
        daemonClient.OnProgressUpdate += async progress =>
        {
            await HandleProgressChangeFromSocketAsync(session, progress);
        };
        daemonClient.OnError += async error =>
        {
            _logger.LogWarning("Socket error for session {SessionId}: {Error}", sessionId, error);
            await Task.CompletedTask;
        };
        daemonClient.OnDisconnected += async () =>
        {
            _logger.LogWarning("Socket disconnected for session {SessionId}", sessionId);
            await Task.CompletedTask;
        };

        // Connect to daemon (socket or TCP)
        await daemonClient.ConnectAsync(cancellationToken);
        _logger.LogInformation("Connected to daemon for session {SessionId}", sessionId);

        session.Client = daemonClient;

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
        await _notifications.NotifyAllBothHubsAsync(SignalREvents.DaemonSessionCreated, sessionDto);

        return session;
    }

    private static string GenerateSocketSecret()
    {
        // 32 bytes -> 64 hex chars, stable ASCII
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
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

        // If already authenticated, don't change state - just check with daemon
        if (session.AuthState == DaemonAuthState.Authenticated)
        {
            _logger.LogInformation("Session {SessionId} is already authenticated, checking daemon status", sessionId);
            var existingChallenge = await session.Client.StartLoginAsync(timeout, cancellationToken);
            if (existingChallenge == null)
            {
                // Daemon confirms we're still logged in
                _logger.LogInformation("Session {SessionId} confirmed authenticated by daemon", sessionId);
                return null;
            }
            // Daemon needs re-authentication - fall through to normal flow
            _logger.LogInformation("Session {SessionId} requires re-authentication", sessionId);
        }

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
            return challenge;
        }

        // If login is already in progress, a challenge might already be queued.
        var pendingChallenge = await session.Client.WaitForChallengeAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (pendingChallenge != null)
        {
            _logger.LogInformation("Received queued challenge for session {SessionId}: Type={Type}, Id={ChallengeId}",
                sessionId, pendingChallenge.CredentialType, pendingChallenge.ChallengeId);
            return pendingChallenge;
        }

        var status = await session.Client.GetStatusAsync(cancellationToken);
        if (status?.Status == "logged-in")
        {
            session.AuthState = DaemonAuthState.Authenticated;
            await NotifyAuthStateChangeAsync(session);
            _logger.LogInformation("Session {SessionId} already authenticated - no challenge needed", sessionId);
            return null;
        }

        if (Directory.Exists(session.ResponsesDir))
        {
            var files = Directory.GetFiles(session.ResponsesDir);
            _logger.LogWarning("No challenge received. Files in responses dir: {Files}",
                files.Length > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : "(empty)");
        }

        throw new InvalidOperationException("No challenge received from daemon. Ensure the prefill daemon image supports TCP on Windows.");
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
            await _notifications.NotifyAllBothHubsAsync(SignalREvents.DaemonSessionUpdated, updatedDto);
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
    /// Attempts auto-login using stored refresh token from SteamAuthStorageService.
    /// Returns (success, error message, username) tuple.
    /// </summary>
    public async Task<(bool success, string? errorMessage, string? username)> TryAutoLoginWithTokenAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return (false, "Session not found", null);
        }

        try
        {
            // Get auth data from storage
            var authData = _steamAuthStorage.GetSteamAuthData();

            // Check if we have valid credentials
            if (authData.Mode != "authenticated" || string.IsNullOrEmpty(authData.RefreshToken))
            {
                _logger.LogInformation("No stored refresh token available for auto-login in session {SessionId}", sessionId);
                return (false, "no_token", null);
            }

            if (string.IsNullOrEmpty(authData.Username))
            {
                _logger.LogWarning("Stored auth data has refresh token but no username for session {SessionId}", sessionId);
                return (false, "invalid_token", null);
            }

            _logger.LogInformation("Attempting auto-login with stored refresh token for user {Username} in session {SessionId}",
                authData.Username, sessionId);

            // Request auto-login challenge from daemon
            var challengeResponse = await session.Client.SendCommandAsync(
                "get-auto-login-challenge",
                timeout: TimeSpan.FromSeconds(30),
                cancellationToken: cancellationToken);

            if (challengeResponse == null || !challengeResponse.Success || challengeResponse.Data == null)
            {
                _logger.LogWarning("Failed to get auto-login challenge from daemon for session {SessionId}: {Error}",
                    sessionId, challengeResponse?.Error ?? "No response");
                return (false, "daemon_error", null);
            }

            // Parse challenge from response data
            var challengeJson = JsonSerializer.Serialize(challengeResponse.Data);
            var challenge = JsonSerializer.Deserialize<CredentialChallenge>(challengeJson);

            if (challenge == null)
            {
                _logger.LogWarning("Failed to parse credential challenge for auto-login in session {SessionId}", sessionId);
                return (false, "parse_error", null);
            }

            // Create JSON payload with username and refresh token
            var credentialPayload = new
            {
                username = authData.Username,
                refreshToken = authData.RefreshToken
            };
            var credentialJson = JsonSerializer.Serialize(credentialPayload);

            // Encrypt the credentials
            var encryptedResponse = SecureCredentialExchange.EncryptCredentialRaw(
                challenge.ChallengeId,
                challenge.ServerPublicKey,
                credentialJson);

            // Send encrypted auto-login credentials to daemon
            var loginResponse = await session.Client.SendCommandAsync(
                "provide-auto-login",
                new Dictionary<string, string>
                {
                    ["challengeId"] = encryptedResponse.ChallengeId,
                    ["clientPublicKey"] = encryptedResponse.ClientPublicKey,
                    ["encryptedCredential"] = encryptedResponse.EncryptedCredential,
                    ["nonce"] = encryptedResponse.Nonce,
                    ["tag"] = encryptedResponse.Tag
                },
                timeout: TimeSpan.FromSeconds(60),
                cancellationToken: cancellationToken);

            if (loginResponse == null)
            {
                _logger.LogWarning("No response from daemon for auto-login in session {SessionId}", sessionId);
                return (false, "no_response", null);
            }

            if (!loginResponse.Success)
            {
                _logger.LogWarning("Auto-login failed for user {Username} in session {SessionId}: {Error}",
                    authData.Username, sessionId, loginResponse.Error ?? loginResponse.Message);
                return (false, loginResponse.Error ?? loginResponse.Message ?? "login_failed", null);
            }

            _logger.LogInformation("Auto-login succeeded for user {Username} in session {SessionId}",
                authData.Username, sessionId);

            // Update session state
            session.AuthState = DaemonAuthState.Authenticated;
            session.SteamUsername = authData.Username;
            await NotifyAuthStateChangeAsync(session);

            // Update database with username
            await _sessionService.SetSessionUsernameAsync(sessionId, authData.Username);

            return (true, null, authData.Username);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during auto-login attempt for session {SessionId}", sessionId);
            return (false, "exception", null);
        }
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

            // Mark session as no longer prefilling and notify frontend
            session.IsPrefilling = false;
            await NotifyPrefillStateChangeAsync(session, "cancelled");

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
    /// Checks cache status by comparing cached depots against Steam manifests.
    /// </summary>
    public async Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<uint> appIds,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (appIds == null || appIds.Count == 0)
        {
            return new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "No app IDs provided" };
        }

        var cachedData = await _cacheService.GetCachedDepotsForAppsAsync(appIds);
        if (cachedData.Count == 0)
        {
            return new CacheStatusResult { Apps = new List<AppCacheStatus>(), Message = "No cached depots found" };
        }

        var cachedDepots = cachedData.Select(d => new CachedDepotInput
        {
            AppId = d.AppId,
            DepotId = d.DepotId,
            ManifestId = d.ManifestId
        }).ToList();

        return await session.Client.CheckCacheStatusAsync(cachedDepots, cancellationToken);
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
        int? maxConcurrency = null,
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
        await _notifications.NotifyAllBothHubsAsync(SignalREvents.DaemonSessionUpdated, startDto);

        try
        {
            // Fetch cached depots from database so daemon knows which games are already up-to-date
            List<CachedDepotInput>? cachedDepots = null;
            if (!force) // Only use cached depots if not forcing re-download
            {
                try
                {
                    var cachedData = await _cacheService.GetAllCachedDepotsAsync();
                    if (cachedData.Count > 0)
                    {
                        cachedDepots = cachedData.Select(d => new CachedDepotInput
                        {
                            AppId = d.AppId,
                            DepotId = d.DepotId,
                            ManifestId = d.ManifestId
                        }).ToList();
                        _logger.LogInformation("Passing {Count} cached depot manifests to daemon for skip detection", cachedDepots.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch cached depots, proceeding without cache data");
                }
            }

            var result = await session.Client.PrefillAsync(all, recent, recentlyPurchased, top, force, operatingSystems, maxConcurrency, cachedDepots, cancellationToken);

            // NOTE: Don't notify completion here - the daemon returns immediately with an acknowledgement.
            // The actual completion is detected by the frontend by counting completed apps.
            // Only notify failure if the command itself failed.
            if (!result.Success)
            {
                await NotifyPrefillStateChangeAsync(session, "failed");
            }

            // Complete the last in-progress entry if any (the final game)
            if (session.CurrentAppId > 0)
            {
                try
                {
                    // Determine status: Skipped if no bytes on success, Failed if error, Completed otherwise
                    string status;
                    if (result.Success && session.CurrentBytesDownloaded == 0)
                    {
                        status = "Skipped";
                    }
                    else if (!result.Success)
                    {
                        status = "Failed";
                    }
                    else
                    {
                        status = "Completed";
                    }

                    await _sessionService.CompletePrefillEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        status,
                        session.CurrentBytesDownloaded,
                        session.CurrentTotalBytes,
                        result.Success ? null : "Prefill ended");

                    _logger.LogInformation("Final app {Status}: {AppId} ({AppName})",
                        status, session.CurrentAppId, session.CurrentAppName);

                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, session.CurrentAppId, status);
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
            await _notifications.NotifyAllBothHubsAsync(SignalREvents.DaemonSessionUpdated, endDto);
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
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(string sessionId, List<string>? operatingSystems = null, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        return await session.Client.GetSelectedAppsStatusAsync(operatingSystems, cancellationToken);
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

        // Complete any in-progress history entry with current bytes before cancelling
        if (session.CurrentAppId > 0)
        {
            try
            {
                await _sessionService.CompletePrefillEntryAsync(
                    session.Id,
                    session.CurrentAppId,
                    "Cancelled",
                    session.CurrentBytesDownloaded,
                    session.CurrentTotalBytes,
                    $"Session terminated: {reason}");

                _logger.LogInformation("Completed in-progress prefill entry for app {AppId} on session termination: {Bytes}/{Total} bytes",
                    session.CurrentAppId, session.CurrentBytesDownloaded, session.CurrentTotalBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete prefill entry for app {AppId} on termination", session.CurrentAppId);
            }
        }

        // Cancel any remaining in-progress history entries (shouldn't be any, but just in case)
        await _sessionService.CancelPrefillEntriesAsync(sessionId);

        // Persist termination to database
        await _sessionService.TerminateSessionAsync(sessionId, reason, terminatedBy);

        session.Status = DaemonSessionStatus.Terminated;
        session.EndedAt = DateTime.UtcNow;

        // Broadcast session termination to all clients for real-time updates (both hubs)
        var terminatedEvent = new { sessionId = session.Id, reason = reason };
        await _notifications.NotifyAllBothHubsAsync(SignalREvents.DaemonSessionTerminated, terminatedEvent);

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
        var configured = _configuration["Prefill:DaemonBasePath"];
        if (!string.IsNullOrEmpty(configured))
        {
            return _pathResolver.ResolvePath(configured);
        }

        return _pathResolver.GetPrefillDirectory();
    }

    private bool ShouldUseTcpMode()
    {
        var configured = _configuration["Prefill:UseTcp"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return OperatingSystem.IsWindows();
            }

            if (bool.TryParse(configured, out var parsed))
            {
                return parsed;
            }

            _logger.LogWarning("Invalid Prefill:UseTcp value '{Value}', falling back to auto.", configured);
        }

        return OperatingSystem.IsWindows();
    }

    private int GetContainerTcpPort()
    {
        var configured = _configuration.GetValue<int?>("Prefill:TcpPort");
        return configured.HasValue && configured.Value > 0 ? configured.Value : DefaultTcpPort;
    }

    private int GetHostTcpPort()
    {
        var configured = _configuration.GetValue<int?>("Prefill:HostTcpPort");
        if (configured.HasValue && configured.Value > 0)
        {
            return configured.Value;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string GetTcpHost()
    {
        return _configuration["Prefill:TcpHost"] ?? "127.0.0.1";
    }

    private string? _cachedHostDataPath;

    private async Task<string> GetHostDataPathAsync(CancellationToken cancellationToken = default)
    {
        // Return cached value if available
        if (_cachedHostDataPath != null)
            return _cachedHostDataPath;

        // Check for explicit configuration first
        var configuredPath = _configuration["Prefill:HostDataPath"];
        if (!string.IsNullOrEmpty(configuredPath) &&
            !string.Equals(configuredPath, "auto", StringComparison.OrdinalIgnoreCase))
        {
            _cachedHostDataPath = _pathResolver.ResolvePath(configuredPath);
            return _cachedHostDataPath;
        }

        if (OperatingSystem.IsWindows())
        {
            _cachedHostDataPath = _pathResolver.GetDataDirectory();
            return _cachedHostDataPath;
        }

        // Auto-detect by inspecting our own container's mounts
        if (_dockerClient != null && _isRunningInContainer)
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
        _cachedHostDataPath = _isRunningInContainer ? "/data" : _pathResolver.GetDataDirectory();
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

    /// <summary>
    /// Gets the lancache DNS IP for prefill containers.
    /// Auto-detects from lancache-dns container if not explicitly configured.
    /// </summary>
    private async Task<string?> GetLancacheDnsIpAsync(CancellationToken cancellationToken = default)
    {
        // Check explicit configuration first
        var configuredIp = _configuration["Prefill:LancacheDnsIp"];
        if (!string.IsNullOrEmpty(configuredIp) &&
            !string.Equals(configuredIp, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configuredIp;
        }

        // Try to auto-detect from lancache-dns container
        if (_dockerClient != null)
        {
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(
                    new ContainersListParameters { All = false }, // Only running containers
                    cancellationToken);

                // Look for lancache-dns container by name patterns
                var dnsContainer = containers.FirstOrDefault(c =>
                    c.Names.Any(n =>
                        n.Contains("lancache-dns", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("lancachedns", StringComparison.OrdinalIgnoreCase) ||
                        (n.Contains("dns", StringComparison.OrdinalIgnoreCase) && n.Contains("lancache", StringComparison.OrdinalIgnoreCase))));

                if (dnsContainer != null)
                {
                    // Check if it's using host networking
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(dnsContainer.ID, cancellationToken);
                    if (inspect.HostConfig.NetworkMode == "host")
                    {
                        // Container uses host networking - return null to indicate we should use host mode
                        _logger.LogInformation("Detected lancache-dns using host networking. " +
                            "Prefill containers will use host network mode for DNS resolution.");
                        return null;
                    }

                    // Get container IP from any network
                    var networks = inspect.NetworkSettings?.Networks;
                    if (networks != null && networks.Count > 0)
                    {
                        var networkWithIp = networks
                            .Where(n => !string.IsNullOrEmpty(n.Value.IPAddress))
                            .FirstOrDefault();

                        if (networkWithIp.Value != null && !string.IsNullOrEmpty(networkWithIp.Value.IPAddress))
                        {
                            _logger.LogInformation("Auto-detected lancache-dns IP: {DnsIp} from container {ContainerName}",
                                networkWithIp.Value.IPAddress, dnsContainer.Names.FirstOrDefault());
                            return networkWithIp.Value.IPAddress;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to auto-detect lancache-dns IP");
            }
        }

        return null;
    }

    /// <summary>
    /// Determines if host network mode should be used based on lancache-dns configuration.
    /// Returns true if lancache-dns uses host networking or if explicitly configured.
    /// </summary>
    private async Task<bool> ShouldUseHostNetworkingAsync(CancellationToken cancellationToken = default)
    {
        // Check explicit configuration first
        var networkMode = _configuration["Prefill:NetworkMode"];
        if (!string.IsNullOrEmpty(networkMode) &&
            !string.Equals(networkMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Using explicit Prefill:NetworkMode configuration: {NetworkMode}", networkMode);
            return networkMode.Equals("host", StringComparison.OrdinalIgnoreCase);
        }

        // Auto-detect: if lancache-dns uses host networking, we should too
        if (_dockerClient != null)
        {
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(
                    new ContainersListParameters { All = false },
                    cancellationToken);

                _logger.LogDebug("Searching for lancache-dns container among {Count} running containers", containers.Count);

                var dnsContainer = containers.FirstOrDefault(c =>
                    c.Names.Any(n =>
                        n.Contains("lancache-dns", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("lancachedns", StringComparison.OrdinalIgnoreCase) ||
                        (n.Contains("dns", StringComparison.OrdinalIgnoreCase) && n.Contains("lancache", StringComparison.OrdinalIgnoreCase))));

                if (dnsContainer != null)
                {
                    _logger.LogInformation("Found lancache-dns container: {ContainerName}", dnsContainer.Names.FirstOrDefault());
                    var inspect = await _dockerClient.Containers.InspectContainerAsync(dnsContainer.ID, cancellationToken);
                    _logger.LogInformation("lancache-dns network mode: {NetworkMode}", inspect.HostConfig.NetworkMode);
                    if (inspect.HostConfig.NetworkMode == "host")
                    {
                        _logger.LogInformation("Detected lancache-dns using host networking. Prefill containers will use host network mode.");
                        return true;
                    }
                }
                else
                {
                    _logger.LogDebug("No lancache-dns container found. Container names searched: {Names}", 
                        string.Join(", ", containers.SelectMany(c => c.Names)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check lancache-dns network mode");
            }
        }

        return false;
    }

    /// <summary>
    /// Gets the network mode for prefill containers.
    /// Options: "host" (use host networking), "bridge" (default), or a custom network name.
    /// </summary>
    private string? GetNetworkMode()
    {
        var networkMode = _configuration["Prefill:NetworkMode"];
        if (string.Equals(networkMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return networkMode;
    }

    /// <summary>
    /// Tests container network connectivity and DNS resolution for lancache domains.
    /// This runs diagnostic commands inside the prefill container to verify:
    /// 1. Internet connectivity (can reach Steam API)
    /// 2. DNS resolution for lancache domains (should point to your cache server)
    /// Results are logged with clear separators for easy troubleshooting.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _cleanupTimer?.Dispose();
        _dockerClient?.Dispose();

        foreach (var session in _sessions.Values)
        {
            session.Client.Dispose();
                session.CancellationTokenSource.Dispose();
        }

        _disposed = true;
    }
}

/// <summary>
/// Represents a Steam Prefill daemon session
/// </summary>

/// <summary>
/// Result of a DNS resolution test for a single domain
/// </summary>
