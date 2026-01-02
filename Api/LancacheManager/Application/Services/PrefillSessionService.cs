using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Application.Services;

/// <summary>
/// Manages Steam Prefill container sessions for users.
/// Each user gets an isolated container running steam-lancache-prefill.
/// </summary>
public class PrefillSessionService : IHostedService, IDisposable
{
    private readonly ILogger<PrefillSessionService> _logger;
    private readonly IHubContext<PrefillTerminalHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, PrefillSession> _activeSessions = new();
    private DockerClient? _dockerClient;
    private Timer? _cleanupTimer;

    // Configuration
    private const string DefaultImage = "tpill90/steam-lancache-prefill:latest";
    private const int DefaultSessionTimeoutMinutes = 120; // 2 hours
    private const int MaxSessionsPerUser = 1;
    private const long DefaultMemoryLimit = 512 * 1024 * 1024; // 512MB
    private const long DefaultCpuLimit = 1_000_000_000; // 1 CPU

    public PrefillSessionService(
        ILogger<PrefillSessionService> logger,
        IHubContext<PrefillTerminalHub> hubContext,
        IConfiguration configuration)
    {
        _logger = logger;
        _hubContext = hubContext;
        _configuration = configuration;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PrefillSessionService starting...");

        // Initialize Docker client
        InitializeDockerClient();

        // Start cleanup timer (every minute)
        _cleanupTimer = new Timer(CleanupExpiredSessions, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

        _logger.LogInformation("PrefillSessionService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("PrefillSessionService stopping...");

        _cleanupTimer?.Change(Timeout.Infinite, 0);

        // Terminate all active sessions
        var sessions = _activeSessions.Values.ToList();
        foreach (var session in sessions)
        {
            await TerminateSessionAsync(session.Id, "Service shutdown");
        }

        _logger.LogInformation("PrefillSessionService stopped");
    }

    private void InitializeDockerClient()
    {
        try
        {
            // Determine Docker endpoint based on OS
            Uri dockerUri;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                dockerUri = new Uri("npipe://./pipe/docker_engine");
            }
            else
            {
                dockerUri = new Uri("unix:///var/run/docker.sock");
            }

            _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
            _logger.LogInformation("Docker client initialized: {Endpoint}", dockerUri);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Docker client");
            throw;
        }
    }

    /// <summary>
    /// Creates a new prefill session for the specified user
    /// </summary>
    public async Task<PrefillSession> CreateSessionAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (_dockerClient == null)
        {
            throw new InvalidOperationException("Docker client not initialized");
        }

        // Check if user already has an active session
        var existingSession = _activeSessions.Values.FirstOrDefault(s => s.UserId == userId && s.Status == PrefillSessionStatus.Active);
        if (existingSession != null)
        {
            throw new InvalidOperationException($"User already has an active session: {existingSession.Id}");
        }

        var session = new PrefillSession
        {
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddMinutes(GetSessionTimeoutMinutes())
        };

        try
        {
            // Ensure image is available
            await EnsureImageExistsAsync(cancellationToken);

            // Get configuration for volume mounts
            var cacheVolume = _configuration["Prefill:CacheVolume"] ?? "/lancache/cache";
            var networkName = _configuration["Prefill:NetworkName"] ?? "lancache";

            // Create container
            var containerName = $"prefill-{userId}-{Guid.NewGuid():N}".Substring(0, 63); // Docker name limit
            session.ContainerName = containerName;

            _logger.LogInformation("Creating prefill container: {ContainerName} for user {UserId}", containerName, userId);

            var createResponse = await _dockerClient.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = GetImageName(),
                    Name = containerName,
                    Tty = true,
                    OpenStdin = true,
                    StdinOnce = false,
                    AttachStdin = true,
                    AttachStdout = true,
                    AttachStderr = true,
                    Cmd = new[] { "/bin/bash" },
                    Env = new[]
                    {
                        "TERM=xterm-256color",
                        "LANG=en_US.UTF-8"
                    },
                    HostConfig = new HostConfig
                    {
                        Binds = new[] { $"{cacheVolume}:/cache:rw" },
                        NetworkMode = networkName,
                        AutoRemove = true,
                        Memory = DefaultMemoryLimit,
                        NanoCPUs = DefaultCpuLimit,
                        // Security: drop all capabilities except what's needed
                        CapDrop = new[] { "ALL" },
                        // Security: read-only root filesystem except for specific paths
                        ReadonlyRootfs = false, // Need write access for Config directory
                    }
                },
                cancellationToken);

            session.ContainerId = createResponse.ID;
            _logger.LogInformation("Container created: {ContainerId}", session.ContainerId);

            // Start container
            var started = await _dockerClient.Containers.StartContainerAsync(
                session.ContainerId,
                new ContainerStartParameters(),
                cancellationToken);

            if (!started)
            {
                throw new Exception("Failed to start container");
            }

            _logger.LogInformation("Container started: {ContainerId}", session.ContainerId);

            // Attach to container stdin/stdout
            session.Stream = await _dockerClient.Containers.AttachContainerAsync(
                session.ContainerId,
                tty: true,
                new ContainerAttachParameters
                {
                    Stdin = true,
                    Stdout = true,
                    Stderr = true,
                    Stream = true
                },
                cancellationToken);

            session.Status = PrefillSessionStatus.Active;
            _activeSessions[session.Id] = session;

            _logger.LogInformation("Prefill session created: {SessionId} for user {UserId}", session.Id, userId);

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create prefill session for user {UserId}", userId);
            session.Status = PrefillSessionStatus.Error;

            // Cleanup on failure
            if (!string.IsNullOrEmpty(session.ContainerId))
            {
                try
                {
                    await _dockerClient.Containers.StopContainerAsync(
                        session.ContainerId,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 1 });
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Sends input to the container's stdin
    /// </summary>
    public async Task SendInputAsync(string sessionId, string input, CancellationToken cancellationToken = default)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (session.Status != PrefillSessionStatus.Active || session.Stream == null)
        {
            throw new InvalidOperationException("Session is not active");
        }

        var bytes = Encoding.UTF8.GetBytes(input);
        await session.Stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
    }

    /// <summary>
    /// Starts reading output from the container and broadcasting to subscribers
    /// </summary>
    public Task StartOutputStreamAsync(string sessionId, string connectionId, CancellationToken cancellationToken = default)
    {
        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (session.Status != PrefillSessionStatus.Active || session.Stream == null)
        {
            throw new InvalidOperationException("Session is not active");
        }

        // Add connection to subscribers
        session.SubscribedConnections.Add(connectionId);

        // If this is the first subscriber, start the output reading task
        if (session.SubscribedConnections.Count == 1)
        {
            _ = Task.Run(async () => await ReadContainerOutputAsync(session), session.CancellationTokenSource.Token);
        }

        return Task.CompletedTask;
    }

    private async Task ReadContainerOutputAsync(PrefillSession session)
    {
        var buffer = new byte[4096];

        try
        {
            while (!session.CancellationTokenSource.Token.IsCancellationRequested &&
                   session.Status == PrefillSessionStatus.Active &&
                   session.Stream != null)
            {
                var result = await session.Stream.ReadOutputAsync(
                    buffer, 0, buffer.Length, session.CancellationTokenSource.Token);

                if (result.Count > 0)
                {
                    var output = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    // Broadcast to all subscribed connections
                    foreach (var connectionId in session.SubscribedConnections.ToList())
                    {
                        try
                        {
                            await _hubContext.Clients.Client(connectionId)
                                .SendAsync("TerminalOutput", session.Id, output, session.CancellationTokenSource.Token);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to send output to connection {ConnectionId}", connectionId);
                            session.SubscribedConnections.Remove(connectionId);
                        }
                    }
                }

                if (result.EOF)
                {
                    _logger.LogInformation("Container output stream ended for session {SessionId}", session.Id);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Output stream reading cancelled for session {SessionId}", session.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading container output for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Resizes the container terminal
    /// </summary>
    public async Task ResizeTerminalAsync(string sessionId, int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (_dockerClient == null) return;

        if (!_activeSessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        if (session.Status != PrefillSessionStatus.Active)
        {
            return;
        }

        try
        {
            await _dockerClient.Containers.ResizeContainerTtyAsync(
                session.ContainerId,
                new ContainerResizeParameters
                {
                    Width = (uint)cols,
                    Height = (uint)rows
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resize terminal for session {SessionId}", sessionId);
        }
    }

    /// <summary>
    /// Terminates a session and stops/removes the container
    /// </summary>
    public async Task TerminateSessionAsync(string sessionId, string reason = "User requested")
    {
        if (!_activeSessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        _logger.LogInformation("Terminating session {SessionId}: {Reason}", sessionId, reason);

        session.Status = PrefillSessionStatus.Terminated;
        session.EndedAt = DateTime.UtcNow;
        session.CancellationTokenSource.Cancel();

        // Notify subscribers
        foreach (var connectionId in session.SubscribedConnections)
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("SessionEnded", sessionId, reason);
            }
            catch
            {
                // Ignore
            }
        }

        // Stop container
        if (_dockerClient != null && !string.IsNullOrEmpty(session.ContainerId))
        {
            try
            {
                await _dockerClient.Containers.StopContainerAsync(
                    session.ContainerId,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 5 });

                _logger.LogInformation("Container stopped: {ContainerId}", session.ContainerId);
            }
            catch (DockerContainerNotFoundException)
            {
                // Container already removed (AutoRemove=true)
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping container {ContainerId}", session.ContainerId);
            }
        }

        session.Stream?.Dispose();
        session.CancellationTokenSource.Dispose();
    }

    /// <summary>
    /// Gets a session by ID
    /// </summary>
    public PrefillSession? GetSession(string sessionId)
    {
        _activeSessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <summary>
    /// Gets all active sessions
    /// </summary>
    public IEnumerable<PrefillSession> GetAllSessions()
    {
        return _activeSessions.Values.ToList();
    }

    /// <summary>
    /// Gets sessions for a specific user
    /// </summary>
    public IEnumerable<PrefillSession> GetUserSessions(string userId)
    {
        return _activeSessions.Values.Where(s => s.UserId == userId).ToList();
    }

    /// <summary>
    /// Removes a connection from session subscribers
    /// </summary>
    public void RemoveSubscriber(string connectionId)
    {
        foreach (var session in _activeSessions.Values)
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
                        _logger.LogDebug("Pull progress: {Status}", msg.Status);
                    }
                }),
                cancellationToken);

            _logger.LogInformation("Image pulled: {ImageName}", imageName);
        }
    }

    private void CleanupExpiredSessions(object? state)
    {
        var expiredSessions = _activeSessions.Values
            .Where(s => s.Status == PrefillSessionStatus.Active && DateTime.UtcNow > s.ExpiresAt)
            .ToList();

        foreach (var session in expiredSessions)
        {
            _logger.LogInformation("Session expired: {SessionId}", session.Id);
            _ = TerminateSessionAsync(session.Id, "Session expired");
        }
    }

    private string GetImageName()
    {
        return _configuration["Prefill:ImageName"] ?? DefaultImage;
    }

    private int GetSessionTimeoutMinutes()
    {
        return _configuration.GetValue<int>("Prefill:SessionTimeoutMinutes", DefaultSessionTimeoutMinutes);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        _dockerClient?.Dispose();

        foreach (var session in _activeSessions.Values)
        {
            session.Stream?.Dispose();
            session.CancellationTokenSource.Dispose();
        }
    }
}
