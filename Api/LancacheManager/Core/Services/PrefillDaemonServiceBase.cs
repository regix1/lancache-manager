using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.Extensions.Options;


namespace LancacheManager.Core.Services;

/// <summary>
/// Abstract base class for managing Prefill daemon Docker containers.
/// Each user session gets its own container with dedicated command/response directories.
/// Uses encrypted credential exchange (ECDH + AES-GCM) for secure authentication.
/// Derived classes provide service-specific configuration (image names, event names, etc.)
/// </summary>
public abstract partial class PrefillDaemonServiceBase : IHostedService, IDisposable
{
    protected readonly ILogger _logger;
    protected readonly ISignalRNotificationService _notifications;
    protected readonly IConfiguration _configuration;
    protected readonly IPathResolver _pathResolver;
    protected readonly IStateService _stateService;
    protected readonly PrefillSessionService _sessionService;
    protected readonly PrefillCacheService _cacheService;
    protected readonly ConcurrentDictionary<string, DaemonSession> _sessions = new();
    protected DockerClient? _dockerClient;
    private bool _disposed;
    protected readonly bool _isRunningInContainer;
    private readonly IOptionsMonitor<PrefillNetworkOptions> _networkOptions;

    /// <summary>
    /// Shared lancache locator (config -&gt; env-file/docker-inspect -&gt; heartbeat-verified
    /// auto-detection). Owns cache-IP resolution and the lancache-dns container bridge-IP lookup that
    /// this class used to duplicate; the same singleton also backs the Status Check feature.
    /// </summary>
    private readonly ILancacheServerLocator _locator;

    /// <summary>
    /// The lancache server IP most recently injected into a daemon container via the
    /// <c>LANCACHE_IP</c> env var, plus the <see cref="LancacheServerLocation.Source"/> it was located
    /// through (config | dockerInspect | envFile | detected | none). Set during container creation
    /// from <see cref="ILancacheServerLocator.LocateAsync(bool, System.Threading.CancellationToken)"/>
    /// and read by the diagnostics builder to surface on the frontend. IP is null when no cache IP
    /// could be determined.
    /// </summary>
    private string? _lastInjectedLancacheIp;
    private string? _lastLancacheIpSource;

    /// <summary>
    /// Serializes the persistent-session start span (reuse-check through container creation) per
    /// service so two concurrent "Start persistent session" calls can never both pass the reuse
    /// check and each create a container (leak M3). One instance per derived service (Steam/Epic/
    /// Xbox/BattleNet/Riot each own their own <see cref="PrefillDaemonServiceBase"/> singleton), so a
    /// single instance-level lock is sufficient - no dictionary keyed by service needed. The guest
    /// session path takes no lock; it has its own semantics (multiple concurrent guest sessions are
    /// expected). Idiom matches <c>CacheClearingService._startLock</c>.
    /// </summary>
    private readonly SemaphoreSlim _persistentStartLock = new(1, 1);

    // Configuration defaults
    private const int DefaultSessionTimeoutMinutes = 120;
    private const int DefaultStallTimeoutSeconds = 180;
    private const int DefaultTcpPort = 45555;

    // Docker labels stamped onto PERSISTENT daemon containers so they can survive a manager restart
    // and be re-adopted (reconnected) instead of being force-removed by the orphan cleanup sweep.
    private const string PersistentLabelKey = "lancache.prefill.persistent";
    private const string ServiceLabelKey = "lancache.prefill.service";
    private const string SessionIdLabelKey = "lancache.prefill.sessionId";
    private const string UserIdLabelKey = "lancache.prefill.userId";

    /// <summary>
    /// Indicates whether Docker is available and connected.
    /// </summary>
    public bool IsDockerAvailable => _dockerClient != null;

    // === Abstract members for service-specific behavior ===

    /// <summary>Service display name (e.g., "Steam", "Epic")</summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Strongly-typed platform identity for this daemon service. Used by <see cref="PrefillAsync"/>
    /// to look up <see cref="ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection"/> so an
    /// OS filter is never forwarded to a daemon that can't act on it.
    /// </summary>
    protected abstract PrefillPlatform Platform { get; }

    /// <summary>Container name prefix (e.g., "steam-daemon-", "epic-daemon-")</summary>
    protected abstract string ContainerPrefix { get; }

    /// <summary>Default Docker image for this service</summary>
    protected abstract string DefaultDockerImage { get; }

    /// <summary>Gets the Docker image name from config with fallback to DefaultDockerImage</summary>
    protected abstract string GetImageName();

    /// <summary>
    /// Admin-configured guest permission duration (hours) for this service. Doubles as the hard
    /// cap on a guest/temporary container's lifetime for this service - the same value gates both
    /// "is this guest still allowed to access this service's prefill tab" (checked at hub connect
    /// time) and "how long can this service's container run before being force-killed" (used below
    /// in the guest env var + expiry stamping), so an admin sets one number per service instead of
    /// two that could silently diverge.
    /// </summary>
    protected abstract int GetGuestPermissionDurationHours();

    /// <summary>
    /// Container path under which the daemon stores its auth/refresh token. The daemon images
    /// declare this directory as a Docker <c>VOLUME</c>, so a non-persistent container gets a fresh
    /// ANONYMOUS volume (wiped on teardown via RemoveVolumes=true). For a persistent session we
    /// instead mount a STABLE NAMED volume at this path (see <see cref="GetPersistentConfigVolumeName"/>)
    /// so the daemon persists its OWN login inside that volume across container/manager restarts. The
    /// manager never reads or injects that auth - it only reads daemon status. Override per service if
    /// the image uses a different config path.
    /// </summary>
    protected virtual string PersistentConfigContainerPath => "/app/Config";

    /// <summary>
    /// Stable, per-service named-volume identifier used to persist a persistent session's auth dir
    /// across container teardown/start. Keyed by service so each platform keeps its own auth.
    /// </summary>
    protected string GetPersistentConfigVolumeName()
        => $"lancache-prefill-persistent-{ServiceName.ToLowerInvariant()}";

    // SignalR event name properties - one for each event
    protected abstract string EventSessionCreated { get; }
    protected abstract string EventSessionUpdated { get; }
    protected abstract string EventSessionTerminated { get; }
    protected abstract string EventAuthStateChanged { get; }
    protected abstract string EventCredentialChallenge { get; }
    protected abstract string EventStatusChanged { get; }
    protected abstract string EventPrefillStateChanged { get; }
    protected abstract string EventPrefillProgress { get; }
    protected abstract string EventPrefillHistoryUpdated { get; }
    protected abstract string EventSessionEnded { get; }

    /// <summary>
    /// Auth state a freshly-created session starts in. Steam/Epic begin
    /// <see cref="DaemonAuthState.NotAuthenticated"/> and require a login step. Anonymous
    /// services (Battle.net) override this to <see cref="DaemonAuthState.Authenticated"/> so the
    /// session is immediately usable and the client never shows a login prompt - the returned DTO
    /// and reconnect/GetMySessions paths report the correct state without waiting on the daemon's
    /// async status update.
    /// </summary>
    protected virtual DaemonAuthState InitialAuthState => DaemonAuthState.NotAuthenticated;

    /// <summary>
    /// Event raised when any prefill daemon session becomes authenticated.
    /// External services subscribe to this to react to daemon auth state changes.
    /// </summary>
    public event Func<Task>? OnDaemonAuthenticated;

    /// <summary>
    /// Event raised when all prefill daemon sessions are no longer authenticated.
    /// External services subscribe to this to react to daemon auth state changes.
    /// </summary>
    public event Func<Task>? OnAllDaemonsLoggedOut;

    /// <summary>
    /// Called when a session becomes authenticated.
    /// Fires the OnDaemonAuthenticated event and calls OnPostAuthenticationAsync for derived class hooks.
    /// </summary>
    protected virtual async Task OnSessionAuthenticatedAsync()
    {
        if (OnDaemonAuthenticated != null)
        {
            try
            {
                await OnDaemonAuthenticated.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnDaemonAuthenticated handler");
            }
        }

        await OnAuthenticatedAsync();
    }

    /// <summary>
    /// Called when all sessions are no longer authenticated.
    /// Fires the OnAllDaemonsLoggedOut event.
    /// </summary>
    protected virtual async Task OnAllSessionsLoggedOutAsync()
    {
        if (OnAllDaemonsLoggedOut != null)
        {
            try
            {
                await OnAllDaemonsLoggedOut.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnAllDaemonsLoggedOut handler");
            }
        }
    }

    /// <summary>
    /// Virtual hook called after OnDaemonAuthenticated fires.
    /// Override in derived classes for service-specific post-authentication behavior.
    /// </summary>
    protected virtual Task OnAuthenticatedAsync() => Task.CompletedTask;

    /// <summary>
    /// Identifies which prefill daemon hub this service routes per-connection and broadcast
    /// notifications to. Steam inherits "steam"; concrete services override for their hub
    /// ("epic", "battlenet"). Used by <see cref="SendToClientAsync"/> and
    /// <see cref="NotifyHubAsync"/> to avoid cross-hub event leakage.
    /// </summary>
    protected virtual string HubRoutingTarget => "steam";

    /// <summary>
    /// HKDF info string for credential encryption. Must match the daemon's SecureCredentialExchange implementation.
    /// Override in derived class if the daemon uses a different info string.
    /// </summary>
    protected virtual string CredentialEncryptionHkdfInfo => "SteamPrefill-Credential-Encryption";

    /// <summary>
    /// URL to test internet connectivity from inside the daemon container.
    /// </summary>
    protected abstract string DiagnosticsConnectivityUrl { get; }

    /// <summary>
    /// DNS domains to test for lancache resolution (should resolve to lancache private IPs).
    /// </summary>
    protected abstract string[] DiagnosticsDnsDomains { get; }

    /// <summary>
    /// Sends a notification to a specific client on the appropriate hub (Steam, Epic, or Battle.net).
    /// </summary>
    protected async Task SendToClientAsync(string connectionId, string eventName, object? data = null)
    {
        switch (HubRoutingTarget)
        {
            case "epic":
                await _notifications.SendToEpicPrefillClientRawAsync(connectionId, eventName, data);
                break;
            case "battlenet":
                await _notifications.SendToBattleNetPrefillClientRawAsync(connectionId, eventName, data);
                break;
            case "riot":
                await _notifications.SendToRiotPrefillClientRawAsync(connectionId, eventName, data);
                break;
            case "xbox":
                await _notifications.SendToXboxPrefillClientRawAsync(connectionId, eventName, data);
                break;
            default:
                await _notifications.SendToPrefillClientRawAsync(connectionId, eventName, data);
                break;
        }
    }

    /// <summary>
    /// Broadcasts a notification to the downloads hub and the correct daemon hub (Steam, Epic, or Battle.net).
    /// Avoids sending service-specific events to the wrong daemon hub.
    /// </summary>
    protected async Task NotifyHubAsync(string eventName, object? data = null)
    {
        switch (HubRoutingTarget)
        {
            case "epic":
                await _notifications.NotifyEpicHubAsync(eventName, data);
                break;
            case "battlenet":
                await _notifications.NotifyBattleNetHubAsync(eventName, data);
                break;
            case "riot":
                await _notifications.NotifyRiotHubAsync(eventName, data);
                break;
            case "xbox":
                await _notifications.NotifyXboxHubAsync(eventName, data);
                break;
            default:
                await _notifications.NotifySteamHubAsync(eventName, data);
                break;
        }
    }

    /// <summary>
    /// Fires an async callback in a fire-and-forget manner with error handling.
    /// </summary>
    protected void FireAndForgetAsync(Func<Task> callback, string callbackName)
    {
        _ = InvokeSafeAsync(callback, callbackName);
    }

    private async Task InvokeSafeAsync(Func<Task> callback, string callbackName)
    {
        try
        {
            await callback.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error notifying {CallbackName}", callbackName);
        }
    }

    protected PrefillDaemonServiceBase(
        ILogger logger,
        ISignalRNotificationService notifications,
        IConfiguration configuration,
        IPathResolver pathResolver,
        IStateService stateService,
        PrefillSessionService sessionService,
        PrefillCacheService cacheService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions,
        ILancacheServerLocator locator)
    {
        _logger = logger;
        _notifications = notifications;
        _configuration = configuration;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _sessionService = sessionService;
        _cacheService = cacheService;
        _networkOptions = networkOptions;
        _locator = locator;
        _isRunningInContainer = bool.TryParse(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), out var inContainer) && inContainer;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{ServiceName}PrefillDaemonService starting...", ServiceName);

        // Initialize Docker client
        try
        {
            Uri dockerUri;
            if (OperatingSystemDetector.IsWindows)
            {
                dockerUri = new Uri("npipe://./pipe/docker_engine");
            }
            else
            {
                dockerUri = new Uri("unix:///var/run/docker.sock");
            }

            // Check if Docker socket exists
            if (!OperatingSystemDetector.IsWindows && !File.Exists("/var/run/docker.sock"))
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
                _logger.LogWarning("{ServiceName} Prefill feature will be disabled - Docker is not available. Start Docker Desktop to enable it.", ServiceName);
                _logger.LogTrace(ex, "Docker connection error details");
                _dockerClient = null;
            }

            // Ensure image is available
            if (_dockerClient != null)
            {
                await EnsureImageExistsAsync(cancellationToken);

                // Cleanup orphaned containers from previous runs
                await CleanupOrphanedContainersAsync(cancellationToken);

                // Re-adopt persistent containers that survived this restart (reconnect, don't recreate)
                await ReadoptPersistentContainersAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Log clean message without stack trace
            _logger.LogWarning("Failed to initialize Docker client - {ServiceName} Prefill feature will be disabled.", ServiceName);
            _logger.LogTrace(ex, "Docker initialization error details");
            _dockerClient = null;
        }

        _logger.LogInformation("{ServiceName}PrefillDaemonService started. Docker available: {DockerAvailable}", ServiceName, _dockerClient != null);
    }

    /// <summary>
    /// Cleans up orphaned prefill daemon containers from previous app runs.
    /// Looks for containers matching THIS service's container prefix pattern.
    /// </summary>
    private async Task CleanupOrphanedContainersAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        try
        {
            // Mark any "Active" sessions in DB as orphaned
            var orphanedSessions = await _sessionService.MarkOrphansAsync();

            // Find all running containers matching this service's prefix
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool>
                        {
                            [ContainerPrefix] = true
                        }
                    }
                },
                cancellationToken);

            if (containers.Count == 0)
            {
                _logger.LogInformation("No orphaned {ServiceName} prefill daemon containers found", ServiceName);
                return;
            }

            _logger.LogWarning("Found {Count} orphaned {ServiceName} prefill daemon containers to cleanup", containers.Count, ServiceName);

            foreach (var container in containers)
            {
                try
                {
                    // Persistent containers that are still running are meant to OUTLIVE a manager
                    // restart: leave them running here and let ReadoptPersistentContainersAsync
                    // reconnect to their daemon socket and rebuild the in-memory session. Only
                    // non-persistent (or stopped) containers are torn down as orphans below.
                    if (IsPersistentLabel(container.Labels) &&
                        string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "Preserving running persistent {ServiceName} container {Name} ({Id}) for re-adoption",
                            ServiceName,
                            container.Names.FirstOrDefault() ?? "unknown",
                            ShortContainerId(container.ID));
                        continue;
                    }

                    // Stop and remove the container
                    if (container.State == "running")
                    {
                        await _dockerClient.Containers.StopContainerAsync(
                            container.ID,
                            new ContainerStopParameters { WaitBeforeKillSeconds = 1 },
                            cancellationToken);
                    }

                    // RemoveVolumes: a login-required daemon (Xbox/Epic) stores its anonymous token
                    // in an anonymous container volume; without RemoveVolumes those volumes linger
                    // after teardown and accumulate. Force kills it if still running.
                    if (!await RemoveContainerForceAsync(container.ID, cancellationToken, removeVolumes: true))
                    {
                        // Already gone, or another sweep is mid-removal - not actually cleaned up by
                        // THIS call, so don't mark it cleaned or log success for it.
                        _logger.LogDebug("Container {Id} removal already in progress or already gone, skipping cleanup mark", ShortContainerId(container.ID));
                        continue;
                    }

                    _logger.LogInformation("Cleaned up orphaned container: {Name} ({Id})",
                        container.Names.FirstOrDefault() ?? "unknown",
                        ShortContainerId(container.ID));

                    // Mark as cleaned in database
                    await _sessionService.MarkOrphanCleanedAsync(container.ID);
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

    /// <summary>
    /// True when the supplied container labels carry <c>lancache.prefill.persistent=true</c>.
    /// </summary>
    private static bool IsPersistentLabel(IDictionary<string, string>? labels)
        => labels != null
           && labels.TryGetValue(PersistentLabelKey, out var value)
           && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Re-adopts persistent prefill daemon containers that survived a manager restart. These containers
    /// are LEFT RUNNING by <see cref="CleanupOrphanedContainersAsync"/> (which only removes
    /// non-persistent / stopped containers). For each still-running container carrying THIS service's
    /// <c>lancache.prefill.persistent=true</c> label, this reconnects to the existing daemon socket and
    /// rebuilds the in-memory <see cref="DaemonSession"/> WITHOUT creating a new container, so the
    /// persistent session reappears (with its daemon-reported auth state) after a restart instead of the
    /// admin having to click Start again. Each container is reconnected in isolation: one failing
    /// container never aborts the others or startup. A labeled container that is not running is left for
    /// the admin to Start.
    /// </summary>
    private async Task ReadoptPersistentContainersAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        try
        {
            // Prefix-based (not exact-name) filter deliberately: this also catches leftover
            // random-suffix persistent containers from before deterministic naming shipped, so the
            // migration below cleans those up too, not just future exact-name duplicates.
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [ContainerPrefix] = true },
                        ["label"] = new Dictionary<string, bool> { [$"{PersistentLabelKey}=true"] = true }
                    }
                },
                cancellationToken);

            if (containers.Count == 0)
            {
                return;
            }

            var decision = PersistentSingletonGates.DecideExistingContainerAction(containers.ToList(), GetAdoptedPersistentContainerIds());

            // Adopt-newest-remove-rest: whenever more than one persistent container matched, every
            // container besides the one we are about to adopt/remove is a leaked duplicate from M1 -
            // clean them all up as a one-time migration regardless of which branch below runs.
            foreach (var extra in decision.ExtrasToRemove)
            {
                _logger.LogWarning(
                    "Startup re-adopt: removing extra/leaked persistent {ServiceName} container {Id}",
                    ServiceName, ShortContainerId(extra.ID));
                await RemoveContainerForceAsync(extra.ID, cancellationToken);
            }

            switch (decision.Action)
            {
                case PersistentContainerAction.Remove:
                    // No running container to adopt - only stopped/leftover ones. Nothing legitimately
                    // running is affected by removing them; CleanupOrphanedContainersAsync would never
                    // touch these (it exempts anything persistent-labeled), so this is the only place
                    // that reaps a dead persistent container left behind by a prior crash.
                    _logger.LogInformation(
                        "Startup re-adopt: removing stopped persistent {ServiceName} container {Id} (no running container to adopt)",
                        ServiceName, ShortContainerId(decision.Target!.ID));
                    await RemoveContainerForceAsync(decision.Target!.ID, cancellationToken);
                    return;

                case PersistentContainerAction.Adopt:
                    break;

                default:
                    // CreateFresh (already adopted or nothing to do) / RetryLater (mid-removal - the
                    // next Start attempt or restart will re-decide) - nothing more to do at startup.
                    return;
            }

            var target = decision.Target!;
            _logger.LogInformation(
                "Re-adopting running persistent {ServiceName} prefill daemon container {Id} after restart",
                ServiceName, ShortContainerId(target.ID));

            try
            {
                await ReconnectPersistentSessionAsync(target, cancellationToken);

                if (GetActivePersistentSession() == null)
                {
                    // ReconnectPersistentSessionAsync has several silent (non-throwing) failure exits
                    // (missing session-id label, missing socket secret, etc.). Left alone, the
                    // container keeps running but is invisible to _sessions forever - exactly leak M1.
                    // Remove it instead so it cannot become an invisible zombie.
                    _logger.LogWarning(
                        "Re-adopt of persistent {ServiceName} container {Id} produced no active session; removing it",
                        ServiceName, ShortContainerId(target.ID));
                    await RemoveContainerForceAsync(target.ID, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to re-adopt persistent {ServiceName} container {Id}; removing it so it cannot become an invisible zombie",
                    ServiceName, ShortContainerId(target.ID));
                await RemoveContainerForceAsync(target.ID, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during persistent {ServiceName} container re-adoption", ServiceName);
        }
    }

    /// <summary>
    /// Reconnects to one already-running persistent daemon container (identified by its
    /// <c>lancache.prefill.*</c> labels) and rebuilds + registers its in-memory session. Recovers the
    /// per-container socket secret and TCP host port from the container's inspected env / port bindings
    /// (these are not held in memory across a manager restart), recomputes the bind-mounted
    /// command/response/socket paths from the session id, and delegates the connect+register to
    /// <see cref="ConnectAndRegisterSessionAsync"/> with <c>isReconnect:true</c>. Never creates a container.
    /// </summary>
    private async Task ReconnectPersistentSessionAsync(ContainerListResponse container, CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        var containerId = container.ID;
        var shortId = ShortContainerId(containerId);

        var labels = container.Labels ?? new Dictionary<string, string>();
        if (!labels.TryGetValue(SessionIdLabelKey, out var sessionId) || string.IsNullOrWhiteSpace(sessionId))
        {
            _logger.LogWarning("Persistent container {Id} is missing the {Label} label; skipping re-adoption", shortId, SessionIdLabelKey);
            return;
        }

        // Defensive: re-adopt runs once on startup, but never adopt the same session twice.
        if (_sessions.ContainsKey(sessionId))
        {
            return;
        }

        labels.TryGetValue(UserIdLabelKey, out var userIdRaw);
        var userId = Guid.TryParse(userIdRaw, out var parsedUserId) ? parsedUserId : Guid.Empty;

        // Recover per-container secrets/ports from the live container (not held across restart).
        var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);
        var env = inspect.Config?.Env ?? new List<string>();

        var socketSecret = GetEnvValue(env, "PREFILL_SOCKET_SECRET");
        if (string.IsNullOrEmpty(socketSecret))
        {
            _logger.LogWarning(
                "Persistent container {Id} has no PREFILL_SOCKET_SECRET in its env; cannot reconnect securely", shortId);
            return;
        }

        var useTcpMode = env.Any(e => e.StartsWith("PREFILL_TCP_PORT=", StringComparison.Ordinal));

        int? tcpHostPort = null;
        if (useTcpMode)
        {
            tcpHostPort = ResolveHostTcpPortFromInspect(inspect);
            if (!tcpHostPort.HasValue)
            {
                _logger.LogWarning(
                    "Persistent container {Id} is in TCP mode but no host port binding was found; cannot reconnect", shortId);
                return;
            }
        }

        // Recompute the bind-mounted command/response dirs + socket path from the session id. These
        // survive the manager restart because they live on the host (bind mounts), and the daemon
        // inside the running container is still listening on the same socket.
        var basePath = GetDaemonBasePath();
        var sessionPath = Path.Combine(basePath, "sessions", sessionId);
        var commandsDir = Path.Combine(sessionPath, "commands");
        var responsesDir = Path.Combine(sessionPath, "responses");
        var socketPath = Path.Combine(responsesDir, "daemon.sock");

        // This method only ever adopts a PERSISTENT container (called from ReadoptPersistentContainersAsync
        // and the persistent create/adopt path), so the fallback (used only when Docker returned no Names,
        // which should not happen in practice) must match the deterministic persistent name, not a
        // sessionId-based guest-shaped name.
        var containerName = container.Names?.FirstOrDefault()?.TrimStart('/') ?? $"{ContainerPrefix}persistent";

        // Preserve the original validity window when the prior DB record is available; otherwise restamp
        // from the admin-configured persistent login validity.
        var dbRecord = await _sessionService.GetSessionAsync(sessionId);
        var createdAtUtc = dbRecord?.CreatedAtUtc ?? DateTime.UtcNow;
        var expiresAt = dbRecord != null && dbRecord.ExpiresAtUtc > DateTime.UtcNow
            ? dbRecord.ExpiresAtUtc
            : DateTime.UtcNow.AddDays(_stateService.GetAdminPersistentLoginValidityDays());

        _logger.LogInformation(
            "Re-adopting persistent {ServiceName} session {SessionId} from running container {Id}",
            ServiceName, sessionId, shortId);

        await ConnectAndRegisterSessionAsync(
            sessionId,
            userId,
            containerId,
            containerName,
            commandsDir,
            responsesDir,
            socketPath,
            socketSecret,
            useTcpMode,
            tcpHostPort,
            createdAtUtc,
            expiresAt,
            isTemporary: false,
            isPersistent: true,
            ipAddress: null,
            userAgent: null,
            networkDiagnostics: null,
            isReconnect: true,
            cancellationToken);
    }


    /// <summary>
    /// Ids of every persistent session currently registered in-memory, keyed by their live
    /// container id. Used by the singleton gate to avoid re-adopting (and thus double-registering)
    /// a container that is already tracked.
    /// </summary>
    private HashSet<string> GetAdoptedPersistentContainerIds()
        => new(_sessions.Values.Where(s => s.IsPersistent).Select(s => s.ContainerId));

    /// <summary>
    /// Lists Docker containers matching this service's deterministic persistent name + label, then
    /// asks <see cref="PersistentSingletonGates.DecideExistingContainerAction"/> what to do about
    /// them. Retries briefly (a few x ~1s) while the decision is <see cref="PersistentContainerAction.RetryLater"/>
    /// (a match is mid-removal, e.g. logout's stop-then-start racing this call) before giving up and
    /// letting the caller proceed to create - <see cref="CreatePersistentContainerWithConflictRetryAsync"/>
    /// is the final backstop against a still-lingering name conflict at that point.
    /// </summary>
    private async Task<PersistentContainerDecision> ResolvePersistentContainerDecisionAsync(CancellationToken cancellationToken)
    {
        var deterministicName = $"{ContainerPrefix}persistent";
        const int maxRetryLaterAttempts = 3;

        for (var attempt = 0; ; attempt++)
        {
            var containers = await _dockerClient!.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [deterministicName] = true },
                        ["label"] = new Dictionary<string, bool> { [$"{PersistentLabelKey}=true"] = true }
                    }
                },
                cancellationToken);

            var decision = PersistentSingletonGates.DecideExistingContainerAction(containers.ToList(), GetAdoptedPersistentContainerIds());

            if (decision.Action != PersistentContainerAction.RetryLater || attempt >= maxRetryLaterAttempts)
            {
                return decision;
            }

            _logger.LogInformation(
                "Persistent {ServiceName} container is mid-removal; retrying in 1s (attempt {Attempt}/{Max})",
                ServiceName, attempt + 1, maxRetryLaterAttempts);
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    /// <summary>
    /// Creates the persistent container, retrying on a 409 "name already in use" conflict (another
    /// container still occupies the deterministic name - e.g. a concurrent removal that
    /// <see cref="ResolvePersistentContainerDecisionAsync"/> raced with). After a few short retries,
    /// force-removes whatever is occupying the name and makes one final create attempt. Follows the
    /// same message-matching <see cref="DockerApiException"/> handling style as <see cref="TerminateSessionAsync"/>.
    /// </summary>
    private async Task<CreateContainerResponse> CreatePersistentContainerWithConflictRetryAsync(
        CreateContainerParameters parameters, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                return await _dockerClient!.Containers.CreateContainerAsync(parameters, cancellationToken);
            }
            catch (DockerApiException ex) when (IsNameConflict(ex))
            {
                _logger.LogWarning(
                    "Persistent container name {Name} still in use (attempt {Attempt}/{Max}); retrying",
                    parameters.Name, attempt + 1, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        _logger.LogWarning(
            "Persistent container name {Name} still conflicting after {Max} retries; force-removing and creating once more",
            parameters.Name, maxRetries);
        await ForceRemoveContainersByExactNameAsync(parameters.Name!, cancellationToken);
        return await _dockerClient!.Containers.CreateContainerAsync(parameters, cancellationToken);
    }

    private static bool IsNameConflict(DockerApiException ex)
        => ex.Message.Contains("Conflict", StringComparison.OrdinalIgnoreCase)
           && ex.Message.Contains("already in use", StringComparison.OrdinalIgnoreCase);

    private async Task ForceRemoveContainersByExactNameAsync(string containerName, CancellationToken cancellationToken)
    {
        var matches = await _dockerClient!.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [containerName] = true }
                }
            },
            cancellationToken);

        foreach (var match in matches.Where(c => (c.Names ?? new List<string>()).Any(n => n.TrimStart('/') == containerName)))
        {
            await RemoveContainerForceAsync(match.ID, cancellationToken);
        }
    }

    /// <summary>
    /// Force-removes a container by id, tolerating the two races already handled elsewhere in this
    /// class (already gone; another sweep mid-removal) - see <see cref="TerminateSessionAsync"/> for
    /// the origin of this exact exception-matching shape. Returns true when this call actually removed
    /// the container, false when removal was swallowed as one of the two known races (so callers that
    /// only want to log/mark-cleaned on genuine success can gate on the result). RemoveVolumes defaults
    /// to false: most callers force-remove a leaked/duplicate/stopped sibling that shares the SAME named
    /// auth volume with a surviving container (<see cref="GetPersistentConfigVolumeName"/>) and must
    /// never wipe it; pass <paramref name="removeVolumes"/> true only for teardown paths that own the
    /// container's volume outright (e.g. non-persistent session termination, orphan cleanup).
    /// </summary>
    private async Task<bool> RemoveContainerForceAsync(string containerId, CancellationToken cancellationToken, bool removeVolumes = false)
    {
        try
        {
            await _dockerClient!.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = removeVolumes },
                cancellationToken);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone.
            return false;
        }
        catch (DockerApiException ex) when (ex.Message.Contains("removal") && ex.Message.Contains("already in progress"))
        {
            // Another sweep/removal is already in flight for this container - fine.
            return false;
        }
    }

    private static string ShortContainerId(string containerId)
        => containerId.Length >= 12 ? containerId[..12] : containerId;

    /// <summary>
    /// Returns the value of a <c>KEY=VALUE</c> entry from a container's inspected env list, or null.
    /// </summary>
    private static string? GetEnvValue(IList<string> env, string key)
    {
        var prefix = key + "=";
        foreach (var entry in env)
        {
            if (entry.StartsWith(prefix, StringComparison.Ordinal))
            {
                return entry[prefix.Length..];
            }
        }
        return null;
    }

    /// <summary>
    /// Reads the published host TCP port from an inspected container's port bindings (host-config first,
    /// then live network settings). Returns null when no positive host port is published.
    /// </summary>
    private static int? ResolveHostTcpPortFromInspect(ContainerInspectResponse inspect)
    {
        static int? FirstHostPort(IDictionary<string, IList<PortBinding>>? bindings)
        {
            if (bindings == null) return null;
            foreach (var kvp in bindings)
            {
                var hostPort = kvp.Value?.FirstOrDefault()?.HostPort;
                if (!string.IsNullOrEmpty(hostPort) && int.TryParse(hostPort, out var port) && port > 0)
                {
                    return port;
                }
            }
            return null;
        }

        return FirstHostPort(inspect.HostConfig?.PortBindings) ?? FirstHostPort(inspect.NetworkSettings?.Ports);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("{ServiceName}PrefillDaemonService stopping...", ServiceName);

        // Terminate all active sessions
        var sessions = _sessions.Values.ToList();
        foreach (var session in sessions)
        {
            await TerminateSessionAsync(session.Id, "Service shutdown");
        }

        _logger.LogInformation("{ServiceName}PrefillDaemonService stopped", ServiceName);
    }

    /// <summary>
    /// Creates a new daemon session for a user.
    /// Spawns a Docker container with dedicated command/response directories.
    /// </summary>
    public async Task<DaemonSession> CreateSessionAsync(
        Guid userId,
        string? ipAddress = null,
        string? userAgent = null,
        SessionType sessionType = SessionType.Admin,
        bool isPersistent = false,
        bool reuseExistingSession = true,
        CancellationToken cancellationToken = default)
    {
        if (_dockerClient == null)
        {
            throw new InvalidOperationException(
                "Docker is not running or not accessible. Please start Docker Desktop and try again.");
        }

        if (!isPersistent)
        {
            return await CreateSessionCoreAsync(userId, ipAddress, userAgent, sessionType, isPersistent, reuseExistingSession, cancellationToken);
        }

        // Persistent starts are serialized per service (this instance) so two concurrent "Start
        // persistent session" calls can never both pass the reuse/adopt checks and each create a
        // container (leak M3): the second caller blocks here, then its reuse-check inside
        // CreateSessionCoreAsync finds the session the first caller just created/adopted. Guest
        // sessions take no lock - multiple concurrent guest sessions are expected.
        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateSessionCoreAsync(userId, ipAddress, userAgent, sessionType, isPersistent, reuseExistingSession, cancellationToken);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }


    private async Task<DaemonSession> CreateSessionCoreAsync(
        Guid userId,
        string? ipAddress,
        string? userAgent,
        SessionType sessionType,
        bool isPersistent,
        bool reuseExistingSession,
        CancellationToken cancellationToken)
    {
        // Check if user already has an active session - return it instead of creating a new one.
        // This match is userId-keyed and only safe because every persistent create path derives userId
        // via DeriveSystemUserId (a single stable system-user id) and always passes
        // reuseExistingSession:true - see PersistentSingletonGates and its callers. A future caller that
        // creates a persistent session for a different, per-admin userId must not reach this reuse check
        // as-is, or it would collide with (and silently return) another admin's persistent session.
        if (reuseExistingSession)
        {
            var existingSession = _sessions.Values.FirstOrDefault(s => s.UserId == userId && s.Status == DaemonSessionStatus.Active);
            if (existingSession != null)
            {
                _logger.LogInformation("Returning existing active session {SessionId} for user {UserId}", existingSession.Id, userId);
                return existingSession;
            }
        }

        // Enforce UserId-based bans at session-create time. For anonymous services (e.g. Battle.net)
        // there is no credential step, so this is the only point at which a ban can be enforced.
        // Username-based (Steam/Epic) bans continue to be enforced at credential-provide time.
        if (await _sessionService.IsUserIdBannedAsync(userId))
        {
            _logger.LogWarning("Refusing to create {ServiceName} session for banned user {UserId}", ServiceName, userId);
            throw new InvalidOperationException("You are banned from using the prefill feature.");
        }

        // Always pull latest image before creating session
        await EnsureImageExistsAsync(cancellationToken);

        if (isPersistent)
        {
            // Error-state replacement (kills leak M2): a socket-disconnect flips a persistent session
            // to Error without tearing its container down; the reuse-check above only matches Active,
            // so left alone every future Start would try to create a duplicate alongside it. Replace
            // it in place instead.
            var existingErrorSession = _sessions.Values.FirstOrDefault(s => PersistentSingletonGates.ShouldReplaceErroredSession(s));
            if (existingErrorSession != null)
            {
                _logger.LogInformation(
                    "Existing persistent {ServiceName} session {SessionId} is in Error state; replacing it",
                    ServiceName, existingErrorSession.Id);
                await TerminateSessionAsync(existingErrorSession.Id, "Replacing errored persistent session", force: true);
            }

            // Docker-level adopt-or-replace pre-create check (kills leak M1's leftover/409 path):
            // an unadopted-but-running container for this service should be reconnected to instead of
            // shadowed by a second container; a stopped one should be removed first so the
            // deterministic name is free.
            var decision = await ResolvePersistentContainerDecisionAsync(cancellationToken);

            foreach (var extra in decision.ExtrasToRemove)
            {
                _logger.LogInformation(
                    "Removing extra leaked persistent {ServiceName} container {Id}",
                    ServiceName, ShortContainerId(extra.ID));
                await RemoveContainerForceAsync(extra.ID, cancellationToken);
            }

            if (decision.Action == PersistentContainerAction.Adopt)
            {
                var target = decision.Target!;
                try
                {
                    await ReconnectPersistentSessionAsync(target, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to adopt existing persistent {ServiceName} container {Id}",
                        ServiceName, ShortContainerId(target.ID));
                }

                var adopted = GetActivePersistentSession();
                if (adopted != null)
                {
                    return adopted;
                }

                // Adoption failed (thrown or one of ReconnectPersistentSessionAsync's silent exits) -
                // the container is now a zombie occupying the deterministic name. Remove it rather
                // than leaving it running and invisible, then fall through to create fresh below.
                _logger.LogWarning(
                    "Adoption of persistent {ServiceName} container {Id} did not produce an active session; removing it and creating fresh",
                    ServiceName, ShortContainerId(target.ID));
                await RemoveContainerForceAsync(target.ID, cancellationToken);
            }
            else if (decision.Action == PersistentContainerAction.Remove)
            {
                _logger.LogInformation(
                    "Removing stopped persistent {ServiceName} container {Id} before creating fresh",
                    ServiceName, ShortContainerId(decision.Target!.ID));
                await RemoveContainerForceAsync(decision.Target!.ID, cancellationToken);
            }
            else if (decision.Action == PersistentContainerAction.RetryLater)
            {
                // ResolvePersistentContainerDecisionAsync's bounded retries were exhausted while a
                // matching container was still mid-removal or restarting. Falling through to create
                // here would either collide with Docker's own in-flight removal for the deterministic
                // name (a 409 that CreatePersistentContainerWithConflictRetryAsync would then have to
                // force through) or force-remove a container that might still recover from a restart
                // loop. Fail the start explicitly instead - the name will be free (or the container
                // will have stabilized) by the time the admin/scheduler retries.
                throw new InvalidOperationException(
                    $"An existing persistent {ServiceName} container is still being removed or restarting. Please try again shortly.");
            }
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

        // Create and start container. Persistent containers get a deterministic name (one per
        // service) so Docker itself enforces the singleton via a 409 name conflict if this fix's
        // pre-create adopt-or-replace check above somehow missed a concurrent creator; guest/temporary
        // containers keep the unique sessionId-based name so many can run at once.
        var containerName = isPersistent ? $"{ContainerPrefix}persistent" : $"{ContainerPrefix}{sessionId}";
        var imageName = GetImageName();

        // Get network configuration for prefill container
        // Auto-detect from lancache-dns container if not explicitly configured
        var useHostNetworking = await ShouldUseHostNetworkingAsync(cancellationToken);
        var lancacheDnsIp = useHostNetworking ? null : await _locator.DetectDnsContainerBridgeIpAsync(cancellationToken);
        var explicitNetworkMode = GetNetworkMode();

        // Build host config with proper network settings
        var binds = new List<string>
        {
            $"{hostCommandsDir}:/commands",
            $"{hostResponsesDir}:/responses"
        };

        // Persistent sessions: pin the daemon's auth/config dir (declared as an anonymous VOLUME
        // by the image) to a STABLE NAMED volume keyed by service. This survives teardown
        // (RemoveVolumes=false on persistent stop) so the daemon's OWN login persists INSIDE the
        // container's volume across container/manager restarts. The manager never injects or transfers
        // any token - the daemon authenticates itself from this volume; otherwise the admin logs in
        // interactively. Temporary/guest containers keep the default anonymous volume and are wiped.
        if (isPersistent)
        {
            binds.Add($"{GetPersistentConfigVolumeName()}:{PersistentConfigContainerPath}");
            _logger.LogInformation(
                "Persistent session {SessionId}: mounting named auth volume {Volume} at {Path}",
                sessionId, GetPersistentConfigVolumeName(), PersistentConfigContainerPath);
        }

        var hostConfig = new HostConfig
        {
            Binds = binds,
            AutoRemove = false  // Temporarily disabled for debugging socket disconnection
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
                "doesn't resolve CDN to lancache. Set Prefill__LancacheDnsIp or Prefill__NetworkMode=host.");
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

        // GUEST/temporary containers get a hard lifetime cap so an abandoned anonymous session
        // self-terminates inside the daemon. Persistent/admin containers are left indefinite
        // (no cap) - their lifecycle is governed by NeedsRelogin + admin teardown.
        if (sessionType == SessionType.Guest)
        {
            var maxLifetimeSeconds = GetGuestPermissionDurationHours() * 3600;
            env.Add($"PREFILL_MAX_LIFETIME_SECONDS={maxLifetimeSeconds}");
            _logger.LogInformation(
                "Guest session {SessionId}: capping daemon lifetime at {Seconds}s",
                sessionId, maxLifetimeSeconds);
        }

        // Inject LANCACHE_IP unconditionally for both host and bridge mode.
        // The daemon honors this env var to bypass container DNS for CDN traffic
        // (URL-rewrite + Host-header spoof). It is NOT a fallback for HostConfig.DNS -
        // they serve different purposes and may be used together or independently.
        // Delegated to the shared locator: config (Prefill__LancacheIp literal/hostname) ->
        // lancache-dns .env/docker-inspect LANCACHE_IP -> heartbeat-verified auto-detect. Passing
        // includeHostSideCandidates:true additionally probes the Docker bridge gateway and
        // host.docker.internal, so a HOST-NETWORKED lancache box now auto-detects too. Loopback is
        // never a cache candidate, so it can never be injected here.
        var lancacheLocation = await _locator.LocateAsync(includeHostSideCandidates: true, cancellationToken);
        var lancacheIp = lancacheLocation.CacheIps.FirstOrDefault();
        _lastInjectedLancacheIp = string.IsNullOrWhiteSpace(lancacheIp) ? null : lancacheIp;
        _lastLancacheIpSource = lancacheLocation.Source;
        if (!string.IsNullOrWhiteSpace(lancacheIp))
        {
            env.Add($"LANCACHE_IP={lancacheIp}");
            _logger.LogInformation(
                "Injecting LANCACHE_IP={Ip} into prefill daemon (source: {Source}) - DNS-independent CDN routing",
                lancacheIp, lancacheLocation.Source);
        }
        else if (string.Equals(lancacheLocation.Source, "config", StringComparison.OrdinalIgnoreCase))
        {
            // Prefill__LancacheIp is configured (non-whitespace) but could not be resolved to an IP.
            // Preserve the original hard-fail contract (the old ResolveLancacheServerIpAsync threw)
            // rather than silently starting a daemon that would route CDN traffic nowhere (H4).
            var configured = _networkOptions.CurrentValue.LancacheIp;
            throw new InvalidOperationException(
                $"Prefill__LancacheIp='{configured}' could not be resolved to an IP address.");
        }
        else
        {
            _logger.LogWarning(
                "Prefill__LancacheIp is not set. The daemon will rely on container DNS to resolve CDN hostnames, " +
                "which may fail in host networking mode or with non-lancache DNS chains. " +
                "Set Prefill__LancacheIp=<your-lancache-server-ip> for reliable operation.");
        }

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

        // Label PERSISTENT containers so a manager restart can re-adopt them (re-attach to the still
        // running daemon socket and rebuild the in-memory session) instead of force-removing them in
        // CleanupOrphanedContainersAsync. Temporary/guest containers are intentionally left unlabeled so
        // they keep being reaped as orphans.
        Dictionary<string, string>? containerLabels = null;
        if (isPersistent)
        {
            containerLabels = new Dictionary<string, string>
            {
                [PersistentLabelKey] = "true",
                [ServiceLabelKey] = ServiceName,
                [SessionIdLabelKey] = sessionId,
                [UserIdLabelKey] = userId.ToString()
            };
        }

        var createParameters = new CreateContainerParameters
        {
            Name = containerName,
            Image = imageName,
            Cmd = cmd,
            Env = env,
            HostConfig = hostConfig,
            Labels = containerLabels,
            ExposedPorts = useTcpMode && tcpContainerPort.HasValue
                ? new Dictionary<string, EmptyStruct> { [$"{tcpContainerPort.Value}/tcp"] = default }
                : null
        };

        var createResponse = isPersistent
            ? await CreatePersistentContainerWithConflictRetryAsync(createParameters, cancellationToken)
            : await _dockerClient!.Containers.CreateContainerAsync(createParameters, cancellationToken);

        var containerId = createResponse.ID;
        _logger.LogInformation("Created container {ContainerId} for session {SessionId}", containerId, sessionId);

        // Start container
        var started = await _dockerClient!.Containers.StartContainerAsync(containerId, null, cancellationToken);
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
        // 1. Internet access to reach the service
        // 2. DNS resolving lancache domains to your cache server
        var networkDiagnostics = await TestContainerConnectivityAsync(containerId, containerName, isHostMode, cancellationToken);

        // Manager-enforced lifetime. Guest/temporary containers are capped at
        // createdAt + GetGuestPermissionDurationHours() so they are reaped by ProcessSessionExpiryAsync
        // exactly when the admin-configured per-service permission duration elapses. That duration is
        // already admin-validated (clamped 1-3h), so it is authoritative on its own - it is NOT further
        // clamped against the generic standard session timeout backstop below (that backstop defaults to
        // 120 minutes and would silently override any per-service duration configured above 2h, which is
        // exactly the bug this comment used to gloss over: the container was correctly told to run for
        // the full configured duration via PREFILL_MAX_LIFETIME_SECONDS, but the manager's own tracked
        // expiry - and therefore the reaper and any UI countdown - was getting cut short). Admin/persistent
        // sessions are never subject to the cap and keep the standard timeout.
        var createdAtUtc = DateTime.UtcNow;
        var isTemporary = sessionType == SessionType.Guest;
        var standardExpiresAt = createdAtUtc.AddMinutes(GetSessionTimeoutMinutes());
        DateTime expiresAt;
        if (isPersistent)
        {
            // Persistent admin login: expiry governs when NeedsRelogin is flagged (the reaper never
            // tears a persistent session down). Use the admin-configured validity window.
            expiresAt = createdAtUtc.AddDays(_stateService.GetAdminPersistentLoginValidityDays());
        }
        else if (isTemporary)
        {
            expiresAt = createdAtUtc.AddHours(GetGuestPermissionDurationHours());
        }
        else
        {
            expiresAt = standardExpiresAt;
        }

        var session = await ConnectAndRegisterSessionAsync(
            sessionId,
            userId,
            containerId,
            containerName,
            commandsDir,
            responsesDir,
            socketPath,
            socketSecret,
            useTcpMode,
            tcpHostPort,
            createdAtUtc,
            expiresAt,
            isTemporary,
            isPersistent,
            ipAddress,
            userAgent,
            networkDiagnostics,
            isReconnect: false,
            cancellationToken);

        return session;
    }

    /// <summary>
    /// Builds the in-memory <see cref="DaemonSession"/> for an already-created/running daemon container,
    /// wires its socket/TCP client event handlers, connects (with retry), registers the session in the
    /// in-memory store, and persists/refreshes the backing <see cref="PrefillSession"/> DB record. Shared
    /// by the normal create path (<see cref="CreateSessionAsync"/>, <paramref name="isReconnect"/>=false)
    /// and the startup re-adopt path (<see cref="ReconnectPersistentSessionAsync"/>,
    /// <paramref name="isReconnect"/>=true) so the connect+register logic lives in exactly one place. The
    /// Docker container itself is created/started by the caller; this method NEVER creates or starts a
    /// container. On reconnect the DB record (just flipped to Orphaned by <c>MarkOrphansAsync</c>) is
    /// reactivated in place instead of inserting a duplicate.
    /// </summary>
    private async Task<DaemonSession> ConnectAndRegisterSessionAsync(
        string sessionId,
        Guid userId,
        string containerId,
        string containerName,
        string commandsDir,
        string responsesDir,
        string socketPath,
        string socketSecret,
        bool useTcpMode,
        int? tcpHostPort,
        DateTime createdAtUtc,
        DateTime expiresAt,
        bool isTemporary,
        bool isPersistent,
        string? ipAddress,
        string? userAgent,
        NetworkDiagnostics? networkDiagnostics,
        bool isReconnect,
        CancellationToken cancellationToken)
    {
        // Parse user agent for OS and browser info
        var (os, browser) = UserAgentParser.Parse(userAgent);

        var session = new DaemonSession
        {
            Id = sessionId,
            UserId = userId,
            AuthState = InitialAuthState,
            ContainerId = containerId,
            ContainerName = containerName,
            CommandsDir = commandsDir,
            ResponsesDir = responsesDir,
            CreatedAt = createdAtUtc,
            ExpiresAt = expiresAt,
            IsTemporary = isTemporary,
            IsPersistent = isPersistent,
            Platform = ServiceName,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            OperatingSystem = os,
            Browser = browser,
            LastSeenAt = DateTime.UtcNow,
            NetworkDiagnostics = networkDiagnostics,
            SocketPath = useTcpMode ? null : socketPath
        };

        // Create daemon client with service-specific HKDF info for credential encryption
        IDaemonClient daemonClient = useTcpMode && tcpHostPort.HasValue
            ? new TcpDaemonClient(GetTcpHost(), tcpHostPort.Value, socketSecret, _logger as ILogger<TcpDaemonClient>)
                { HkdfInfo = CredentialEncryptionHkdfInfo }
            : new SocketDaemonClient(socketPath, socketSecret, _logger as ILogger<SocketDaemonClient>)
                { HkdfInfo = CredentialEncryptionHkdfInfo };

        // Wire up socket events to session handlers
        daemonClient.OnCredentialChallenge += async (CredentialChallenge challenge) =>
        {
            await OnCredentialChallengeAsync(session, challenge);
        };
        daemonClient.OnStatusUpdate += async (DaemonStatus status) =>
        {
            await OnStatusChangeAsync(session, status);
        };
        daemonClient.OnProgressUpdate += async (SocketPrefillProgress progress) =>
        {
            await OnProgressChangeAsync(session, progress);
        };
        daemonClient.OnError += async (string error) =>
        {
            _logger.LogWarning("Socket error for session {SessionId}: {Error}", sessionId, error);
            await Task.CompletedTask;
        };
        daemonClient.OnDisconnected += async () =>
        {
            _logger.LogWarning("Socket disconnected unexpectedly for session {SessionId}", sessionId);

            if (_sessions.TryGetValue(sessionId, out var disconnectedSession))
            {
                disconnectedSession.Status = DaemonSessionStatus.Error;
                disconnectedSession.ErrorMessage = "Socket connection lost unexpectedly";

                try
                {
                    // If a prefill was in flight when the socket died, the in-flight `prefill`
                    // command already returned (the ack is immediate), so nothing else would flip
                    // the prefill terminal - leaving a ghost IsPrefilling=true with no terminal
                    // event. Route through the single idempotent terminal funnel (→ Failed) so the
                    // user's bar resolves and IsPrefilling is cleared. Idempotent, so a later daemon
                    // terminal event (if the socket reconnects) cannot double-fire.
                    if (disconnectedSession.IsPrefilling)
                    {
                        await TransitionToTerminalAsync(disconnectedSession, PrefillState.Failed);
                    }

                    var sessionDto = DaemonSessionDto.FromSession(disconnectedSession);
                    await NotifyHubAsync(EventSessionUpdated, sessionDto);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to notify frontend of disconnect for session {SessionId}", sessionId);
                }
            }
        };

        // Connect to daemon (socket or TCP) with retry
        const int maxRetries = 3;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Connecting to daemon for session {SessionId} (attempt {Attempt}/{MaxRetries})",
                    sessionId, attempt, maxRetries);
                await daemonClient.ConnectAsync(cancellationToken);
                _logger.LogInformation("Connected to daemon for session {SessionId}", sessionId);
                break;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Socket connection attempt {Attempt} failed for session {SessionId}, retrying...",
                    attempt, sessionId);

                // Fetch daemon container logs to diagnose why the connection failed
                await LogContainerLogsAsync(containerId, sessionId, cancellationToken);

                // Wait before retry with increasing delay
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "All {MaxRetries} socket connection attempts failed for session {SessionId}", maxRetries, sessionId);

                // Fetch daemon container logs to diagnose why the connection failed
                await LogContainerLogsAsync(containerId, sessionId, cancellationToken);

                throw;
            }
        }

        session.Client = daemonClient;

        // Belt-and-braces: guarantee a brand NEW persistent container (never a re-adopted one) cannot
        // silently inherit a stale login left in its named auth volume by a previous life (e.g. a
        // crash/reboot where TerminateSessionAsync's stop-side logout never got a chance to run). Runs
        // BEFORE the session is registered/persisted/broadcast below, so nothing - a SignalR listener
        // reacting to EventSessionCreated, a status poll - can ever observe this session (and therefore
        // attempt a login against it) until any stale login has already been erased.
        await ApplyFreshPersistentLoginGuardAsync(session, isPersistent, isReconnect);

        _sessions[sessionId] = session;

        // Persist session to database for admin visibility and orphan tracking. On reconnect the backing
        // record was just flipped to Orphaned by MarkOrphansAsync, so reactivate it IN PLACE (reusing the
        // same unique SessionId keeps the PrefillHistory linkage) rather than inserting a duplicate.
        if (isReconnect)
        {
            await _sessionService.ReactivateSessionAsync(
                sessionId,
                userId,
                containerId,
                containerName,
                session.ExpiresAt,
                ServiceName);
        }
        else
        {
            await _sessionService.CreateSessionAsync(
                sessionId,
                userId,
                containerId,
                containerName,
                session.ExpiresAt,
                ServiceName);
        }

        _logger.LogInformation(
            "{Action} daemon session {SessionId} for user {UserId}",
            isReconnect ? "Re-adopted" : "Created", sessionId, userId);

        // A re-adopted persistent container keeps its login inside its named auth volume across a
        // manager restart, but this fresh in-memory session starts at InitialAuthState
        // (NotAuthenticated for account services). Reconcile once with the daemon's LIVE status,
        // routed through the same OnStatusChangeAsync transition every socket status push uses, so
        // a still-logged-in container immediately reads as Authenticated (and broadcasts
        // AuthStateChanged) instead of showing needs-login until the next interactive login.
        // Best-effort: an unresponsive daemon leaves the conservative NotAuthenticated default.
        if (isReconnect && isPersistent)
        {
            try
            {
                var liveStatus = await daemonClient.GetStatusAsync(cancellationToken);
                if (liveStatus is not null)
                {
                    await OnStatusChangeAsync(session, liveStatus);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to reconcile adopted persistent {ServiceName} session {SessionId} with live daemon status",
                    ServiceName, sessionId);
            }
        }

        // Broadcast session creation to all clients for real-time updates (both hubs)
        var sessionDtoCreated = DaemonSessionDto.FromSession(session);
        await NotifyHubAsync(EventSessionCreated, sessionDtoCreated);

        return session;
    }

    /// <summary>
    /// Best-effort, bounded logout of a persistent session's live daemon socket. Used both when a
    /// persistent session is stopped (<see cref="TerminateSessionAsync"/>) and, as a belt-and-braces
    /// guard, right after a brand new persistent container connects for the first time (see
    /// <see cref="ApplyFreshPersistentLoginGuardAsync"/>). A dead/hung socket must never block the
    /// caller's teardown or startup flow, so every failure (timeout, transport error, or the daemon
    /// reporting failure) is swallowed and logged - callers proceed regardless. No-op for
    /// non-persistent sessions.
    /// </summary>
    private async Task TryBestEffortLogoutAsync(DaemonSession session, string context)
    {
        if (!session.IsPersistent)
        {
            return;
        }

        try
        {
            // 15s, not 5s: when logout races an in-flight login, the daemon (Steam/Epic/Xbox) cancels
            // the login task and AWAITS its unwind before acking - up to an 8s LogoutLoginTaskTimeout
            // on the daemon side (Xbox's chain through PollForTokenAsync + the XSTS/XASU token mint is
            // the slowest). A shorter caller-side timeout here fires before that unwind can ever
            // finish, so every stop-during-login logged a spurious "failed; proceeding anyway" even
            // though the daemon was about to answer. This is linked (CancellationTokenSource.
            // CreateLinkedTokenSource in SocketDaemonClient.SendCoreAsync) with LogoutWithReasonAsync's
            // own 10s->15s command timeout - keep both in sync, comfortably above the 8s budget.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var outcome = await session.Client.LogoutWithReasonAsync(cts.Token);
            if (!outcome.Success && outcome.RequiresLogin)
            {
                // Older daemon image: its pre-login command gate rejects "logout" outright while the
                // session hasn't finished authenticating (see the erase-on-stop regression diagnosis) -
                // this is expected for a container being cancelled mid-challenge, not a real failure.
                _logger.LogInformation(
                    "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}): " +
                    "daemon declined logout before authentication (older daemon image); nothing to log out",
                    ServiceName, session.Id, context);
            }
            else
            {
                _logger.LogInformation(
                    "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}): {Result}",
                    ServiceName, session.Id, context, outcome.Success ? "acknowledged" : "daemon reported failure");
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Best-effort logout for persistent {ServiceName} session {SessionId} ({Context}) failed; proceeding anyway",
                ServiceName, session.Id, context);
        }
    }

    /// <summary>
    /// Belt-and-braces guard called once, right after a persistent session's daemon socket first
    /// connects: forces one best-effort logout for a NEWLY CREATED persistent container (never a
    /// re-adopted one) so it can never silently inherit a stale login left in its named auth volume by
    /// a previous life (e.g. a crash/reboot where <see cref="TerminateSessionAsync"/>'s stop-side
    /// logout never got a chance to run). No-op for non-persistent sessions and for re-adopted
    /// sessions - an adopted, still-running container's login IS its current life and must be
    /// preserved. Internal (not private) so tests can exercise the decision seam directly without
    /// standing up a real Docker container and daemon socket - see
    /// <c>InternalsVisibleTo("LancacheManager.Tests")</c> on the containing project.
    /// </summary>
    internal Task ApplyFreshPersistentLoginGuardAsync(DaemonSession session, bool isPersistent, bool isReconnect)
    {
        if (!isPersistent || isReconnect)
        {
            return Task.CompletedTask;
        }

        return TryBestEffortLogoutAsync(session, "fresh persistent container create");
    }

    /// <summary>
    /// Fetches and logs the daemon container's stdout/stderr to help diagnose socket connection failures.
    /// </summary>
    private async Task LogContainerLogsAsync(string containerId, string sessionId, CancellationToken cancellationToken)
    {
        if (_dockerClient == null) return;

        try
        {
            // Check if container is still running
            var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);
            _logger.LogWarning("Daemon container state for session {SessionId}: Running={Running}, Status={Status}, ExitCode={ExitCode}",
                sessionId, inspect.State.Running, inspect.State.Status, inspect.State.ExitCode);

            var logParams = new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "50" };
            using var logStream = await _dockerClient.Containers.GetContainerLogsAsync(containerId, false, logParams, cancellationToken);
            using var memoryStream = new MemoryStream();
            await logStream.CopyOutputToAsync(null, memoryStream, null, cancellationToken);
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream);
            var logs = await reader.ReadToEndAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(logs))
            {
                _logger.LogWarning("Daemon container logs for session {SessionId}:\n{Logs}", sessionId, logs);
            }
            else
            {
                _logger.LogWarning("Daemon container produced no logs for session {SessionId}", sessionId);
            }
        }
        catch (DockerContainerNotFoundException)
        {
            _logger.LogWarning("Daemon container {ContainerId} already removed (AutoRemove) for session {SessionId}", containerId, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not retrieve daemon container logs for session {SessionId}", sessionId);
        }
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

        // Serializes login attempts on this session (Cursor #3): overlapping calls would race the
        // daemon's single challenge/status stream and clobber each other's failure text/auth state.
        // A second concurrent attempt is rejected outright (try-acquire, never queued) so the caller
        // gets an immediate, unambiguous error instead of silently waiting behind one it doesn't know
        // about.
        if (!await session.LoginLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException($"A login attempt is already in progress for session {sessionId}.");
        }

        var abandonedLoginCleanup = new AbandonedLoginCleanupHolder();
        try
        {
            return await StartLoginCoreAsync(session, sessionId, timeout, abandonedLoginCleanup, cancellationToken);
        }
        finally
        {
            // Normally releases immediately. But when a fail-fast completion abandoned a still-running
            // daemon task (Cursor #1), AwaitChallengeOrLoginFailureAsync stashed its cleanup task here
            // instead of awaiting it inline - the real CancelLoginAsync clears the pending challenge
            // wait synchronously but then also awaits its own "cancel-login" command round-trip to the
            // daemon (up to that command's own timeout), so it must never be awaited on the hot
            // fail-fast return path. The lock is released only once that cleanup finishes, so a later
            // login attempt on this session can never overlap with the abandoned one (Cursor #1+#3).
            if (abandonedLoginCleanup.Task is { } cleanupTask)
            {
                _ = cleanupTask.ContinueWith(_ => session.LoginLock.Release(), TaskScheduler.Default);
            }
            else
            {
                session.LoginLock.Release();
            }
        }
    }

    /// <summary>
    /// Mutable single-slot holder used to pass an abandoned-login-task cleanup <see cref="Task"/> out of
    /// <see cref="AwaitChallengeOrLoginFailureAsync"/> to <see cref="StartLoginAsync"/>'s <c>finally</c>
    /// without adding permanent state to <see cref="DaemonSession"/> - it only exists for the lifetime
    /// of one <see cref="StartLoginAsync"/> call.
    /// </summary>
    private sealed class AbandonedLoginCleanupHolder
    {
        public Task? Task;
    }

    private async Task<CredentialChallenge?> StartLoginCoreAsync(
        DaemonSession session, string sessionId, TimeSpan? timeout, AbandonedLoginCleanupHolder abandonedLoginCleanup, CancellationToken cancellationToken)
    {
        // Resume path: a challenge from an earlier StartLoginAsync call on this session is still
        // pending (e.g. the frontend closed/reopened the login modal, or a second request raced in
        // before the first one's challenge was consumed). Answer with the SAME challenge and issue NO
        // daemon command at all - the daemon would only reply "already in progress" without
        // re-emitting it, and the client's own StartLoginAsync destroys its queued copy the moment
        // it's invoked again, so this check must run before anything touches session.Client.
        if (session.PendingLoginChallenge is { } pendingLoginChallenge && session.AuthState != DaemonAuthState.Authenticated)
        {
            _logger.LogInformation(
                "Session {SessionId} has a pending login challenge - resuming it instead of starting a new daemon login",
                sessionId);
            return pendingLoginChallenge;
        }

        _logger.LogInformation("Starting login for session {SessionId}. ResponsesDir: {ResponsesDir}",
            sessionId, session.ResponsesDir);

        // Reset any stale failure text from a previous attempt before racing this one.
        session.LastLoginFailureMessage = null;

        // A fresh attempt gets brand-new challenge ids from the daemon, so clear the just-consumed
        // marker left by any earlier attempt/step - it must only ever name the single most-recently
        // consumed challenge of the CURRENT flow, so it can never suppress a legitimate future
        // challenge. (The resume path above returns before here, so a mid-flow resume keeps it.)
        session.LastConsumedLoginChallengeId = null;

        // The daemon broadcasts "Login failed: <reason>" (status "awaiting-login") within
        // milliseconds when it can't proceed (e.g. an undecryptable stored token). Racing that
        // broadcast against the blind challenge waits below lets a doomed login fail in seconds
        // with the daemon's real error text instead of burning the full 30s/10s wait chain.
        var loginFailureTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<DaemonStatus, Task> onDaemonStatusUpdate = status =>
        {
            if (status != null && TryGetLoginFailureMessage(status, out var failureMessage))
            {
                loginFailureTcs.TrySetResult(failureMessage);
            }
            return Task.CompletedTask;
        };

        session.Client.OnStatusUpdate += onDaemonStatusUpdate;
        try
        {
            // If already authenticated, don't change state - just check with daemon
            if (session.AuthState == DaemonAuthState.Authenticated)
            {
                _logger.LogInformation("Session {SessionId} is already authenticated, checking daemon status", sessionId);
                var existingChallenge = await AwaitChallengeOrLoginFailureAsync(
                    session, session.Client.StartLoginAsync(timeout, cancellationToken), loginFailureTcs, abandonedLoginCleanup);
                if (loginFailureTcs.Task.IsCompleted)
                {
                    return await FailLoginFastAsync(session, sessionId, loginFailureTcs.Task.Result);
                }
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

            var challenge = await AwaitChallengeOrLoginFailureAsync(
                session, session.Client.StartLoginAsync(timeout, cancellationToken), loginFailureTcs, abandonedLoginCleanup);
            if (loginFailureTcs.Task.IsCompleted)
            {
                return await FailLoginFastAsync(session, sessionId, loginFailureTcs.Task.Result);
            }

            // Log result
            if (challenge != null)
            {
                _logger.LogInformation("Received challenge for session {SessionId}: Type={Type}, Id={ChallengeId}",
                    sessionId, challenge.CredentialType, challenge.ChallengeId);
                session.PendingLoginChallenge = challenge;
                return challenge;
            }

            // If login is already in progress, a challenge might already be queued.
            var pendingChallenge = await AwaitChallengeOrLoginFailureAsync(
                session, session.Client.WaitForChallengeAsync(TimeSpan.FromSeconds(10), cancellationToken), loginFailureTcs, abandonedLoginCleanup);
            if (loginFailureTcs.Task.IsCompleted)
            {
                return await FailLoginFastAsync(session, sessionId, loginFailureTcs.Task.Result);
            }
            if (pendingChallenge != null)
            {
                _logger.LogInformation("Received queued challenge for session {SessionId}: Type={Type}, Id={ChallengeId}",
                    sessionId, pendingChallenge.CredentialType, pendingChallenge.ChallengeId);
                session.PendingLoginChallenge = pendingChallenge;
                return pendingChallenge;
            }

            var status = await session.Client.GetStatusAsync(cancellationToken);
            if (status?.Status == "logged-in")
            {
                session.AuthState = DaemonAuthState.Authenticated;
                await NotifyAuthStateChangeAsync(session);

                // Notify derived class that a session is now authenticated
                FireAndForgetAsync(OnSessionAuthenticatedAsync, nameof(OnSessionAuthenticatedAsync));

                _logger.LogInformation("Session {SessionId} already authenticated - no challenge needed", sessionId);
                return null;
            }

            if (Directory.Exists(session.ResponsesDir))
            {
                var files = Directory.GetFiles(session.ResponsesDir);
                _logger.LogWarning("No challenge received. Files in responses dir: {Files}",
                    files.Length > 0 ? string.Join(", ", files.Select(Path.GetFileName)) : "(empty)");
            }

            // Defensive guard: a persistent container may already be authenticated from its own named auth
            // volume (the daemon self-authenticates; the manager never injects a token). In that case the
            // daemon emits no challenge. Re-check the live daemon status before failing - if it reports
            // logged-in, treat this as an already-authenticated result (no challenge needed) rather than
            // throwing, so a stray login attempt on an already-logged-in container does not error.
            var finalStatus = await session.Client.GetStatusAsync(cancellationToken);
            if (finalStatus?.Status == "logged-in")
            {
                session.AuthState = DaemonAuthState.Authenticated;
                await NotifyAuthStateChangeAsync(session);
                FireAndForgetAsync(OnSessionAuthenticatedAsync, nameof(OnSessionAuthenticatedAsync));
                _logger.LogInformation(
                    "Session {SessionId} already authenticated per daemon status - no challenge needed", sessionId);
                return null;
            }

            _logger.LogWarning(
                "Session {SessionId} login started but no challenge was received from the daemon",
                sessionId);
            return null;
        }
        finally
        {
            session.Client.OnStatusUpdate -= onDaemonStatusUpdate;
        }
    }

    /// <summary>
    /// Races a daemon challenge-producing call against <paramref name="loginFailureTcs"/>. Returns the
    /// daemon's result when it wins the race; returns null when the failure broadcast wins - the
    /// caller checks <c>loginFailureTcs.Task.IsCompleted</c> to distinguish "daemon said no challenge
    /// yet" from "daemon reported a login failure".
    /// </summary>
    /// <remarks>
    /// When the failure broadcast wins, <paramref name="daemonTask"/> (the daemon client's
    /// StartLoginAsync/WaitForChallengeAsync call) is still running. Returning immediately here -
    /// rather than awaiting its cleanup inline - preserves the fail-fast latency guarantee: the real
    /// <see cref="IDaemonClient.CancelLoginAsync"/> clears the pending challenge wait synchronously but
    /// then also awaits its own "cancel-login" command round-trip to the daemon, which can itself take
    /// up to that command's own timeout - awaiting it here would reintroduce the exact blind-wait
    /// latency this fix eliminates. Instead the cancel+observe work is stashed on
    /// <paramref name="abandonedLoginCleanup"/> for <see cref="StartLoginAsync"/> to await before
    /// releasing <see cref="DaemonSession.LoginLock"/> (Cursor #1+#3 together): a later login attempt
    /// on this session is rejected by the lock until the abandoned task is fully resolved, so it can
    /// never overlap with it.
    /// </remarks>
    private async Task<CredentialChallenge?> AwaitChallengeOrLoginFailureAsync(
        DaemonSession session,
        Task<CredentialChallenge?> daemonTask,
        TaskCompletionSource<string> loginFailureTcs,
        AbandonedLoginCleanupHolder abandonedLoginCleanup)
    {
        var completed = await Task.WhenAny(daemonTask, loginFailureTcs.Task);
        if (completed != loginFailureTcs.Task)
        {
            return await daemonTask;
        }

        abandonedLoginCleanup.Task = ObserveAbandonedLoginTaskAsync(session, daemonTask);
        return null;
    }

    /// <summary>
    /// Best-effort cancels the daemon's in-flight login wait and then awaits it to completion,
    /// swallowing any resulting exception so it is never left unobserved (Cursor #1). Always uses
    /// <see cref="CancellationToken.None"/> for the cancel command itself - this cleanup must run to
    /// completion regardless of whether the original caller's request/token has since been cancelled
    /// or disposed.
    /// </summary>
    private async Task ObserveAbandonedLoginTaskAsync(DaemonSession session, Task<CredentialChallenge?> daemonTask)
    {
        try
        {
            await session.Client.CancelLoginAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "CancelLoginAsync failed while abandoning a fail-fast-superseded login task for session {SessionId}",
                session.Id);
        }

        try
        {
            await daemonTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Abandoned login task for session {SessionId} completed with an exception after fail-fast cancellation (expected)",
                session.Id);
        }
    }

    /// <summary>
    /// Matches the daemon's uniform failure broadcast shape (steam/epic/xbox all call
    /// <c>BroadcastStatusAsync("awaiting-login", $"Login failed: {ex.Message}")</c> on a login exception).
    /// </summary>
    private static bool TryGetLoginFailureMessage(DaemonStatus status, out string failureMessage)
    {
        if (!string.IsNullOrEmpty(status.Message) &&
            status.Message.Contains("Login failed:", StringComparison.OrdinalIgnoreCase))
        {
            failureMessage = status.Message;
            return true;
        }
        failureMessage = string.Empty;
        return false;
    }

    /// <summary>
    /// Terminal handler for a fail-fast login: records the daemon's real error text on the session
    /// (read directly by <c>PersistentPrefillController.StartLoginAsync</c>), resets auth state since the
    /// login definitively failed, and broadcasts the state change so the "authenticating" UI clears.
    /// </summary>
    protected async Task<CredentialChallenge?> FailLoginFastAsync(DaemonSession session, string sessionId, string failureMessage)
    {
        session.LastLoginFailureMessage = failureMessage;
        session.AuthState = DaemonAuthState.NotAuthenticated;
        // A stale pending challenge must never be handed to a resume after the daemon has reported a
        // failure for this attempt.
        ClearPendingLoginChallenge(session);
        _logger.LogWarning("Session {SessionId} login failed fast: {FailureMessage}", sessionId, failureMessage);
        await NotifyAuthStateChangeAsync(session);
        return null;
    }

    /// <summary>
    /// Clears a session's cached resumable login challenge (<see cref="DaemonSession.PendingLoginChallenge"/>),
    /// returning whatever it held. A stale challenge must never be served to a later resume, so every
    /// transition that invalidates it - a fail-fast failure, a credential being consumed, a cancelled
    /// login, an auth-state move to Authenticated, or session termination - goes through this single
    /// place rather than a direct <c>PendingLoginChallenge = null</c> write at each call site.
    /// </summary>
    private static CredentialChallenge? ClearPendingLoginChallenge(DaemonSession session)
    {
        var previous = session.PendingLoginChallenge;
        session.PendingLoginChallenge = null;
        return previous;
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge.
    /// Override in derived class to add service-specific credential handling (e.g., ban checking).
    /// </summary>
    public virtual async Task ProvideCredentialAsync(
        string sessionId,
        CredentialChallenge challenge,
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        // The caller is answering the session's current pending challenge - drop the cache here so
        // no concurrent resume/poll (GET /challenge, the reopen reconcile, a SignalR-down poll
        // fallback) can serve this now-consumed challenge as if it were still live. If the daemon
        // needs another step, OnCredentialChallengeAsync re-populates the cache with that follow-on
        // challenge; if not, WaitForChallengeAsync correctly falls through to a live daemon wait
        // instead of replaying stale data.
        ClearPendingLoginChallenge(session);

        // Record the id of the challenge we are answering so a concurrent stale re-delivery of this
        // SAME challenge over the daemon's OTHER delivery channel (the OnCredentialChallenge event,
        // handled by OnCredentialChallengeAsync) cannot re-cache the now-consumed challenge after this
        // clear - that late duplicate would otherwise be replayed to the next WaitForChallengeAsync/GET
        // poll (the "challenge:password twice" race). Only a genuinely NEW follow-on challenge (a
        // different ChallengeId) is allowed to repopulate the cache; a matching id is dropped as the
        // stale second copy of the challenge just answered. Reset on the next fresh login attempt.
        session.LastConsumedLoginChallengeId = challenge.ChallengeId;

        // If this is the username credential, capture it
        if (challenge.CredentialType.Equals("username", StringComparison.OrdinalIgnoreCase))
        {
            session.SteamUsername = credential;
            session.Username = credential;

            // Update the database record with the username
            await _sessionService.SetUsernameAsync(sessionId, credential);

            // Do not log the username at Information level - it is PII. The value is
            // persisted via SetUsernameAsync above; keep only the session id in the log.
            _logger.LogDebug("Captured username for session {SessionId}", sessionId);

            // Broadcast session update to all clients for real-time updates (both hubs)
            var updatedDto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(EventSessionUpdated, updatedDto);
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

        // Serve a cached resume challenge immediately - no need to make the poller wait on the
        // daemon when we already know the answer.
        if (session.PendingLoginChallenge is { } pendingLoginChallenge && session.AuthState != DaemonAuthState.Authenticated)
        {
            return pendingLoginChallenge;
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

        // Clear the resume cache and any queued daemon-side challenge FIRST, before the (possibly
        // slow) daemon cancel round-trip below. CancelLoginAsync takes no lock, so a concurrent
        // resume/poll landing during that await would otherwise be able to serve a challenge that is
        // being cancelled out from under it - a cancelled login must never be resumable, not even for
        // the few ms the daemon round-trip takes. Captured first so it can be restored if the daemon
        // round-trip itself fails below - a failed cancel must not be treated as a successful one.
        var capturedPendingChallenge = ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        try
        {
            // Send cancel-login command to daemon - this will abort any pending credential waits
            await session.Client.CancelLoginAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The daemon round-trip failed, so the daemon may still believe this login is in
            // progress. Restore the cached challenge so a later StartLoginAsync resumes it instead of
            // racing a brand-new daemon login against the one that was never actually cancelled - the
            // exact duplicate-login race the resume cache exists to prevent - and surface the failure
            // to the caller instead of silently proceeding as if the cancel had succeeded.
            session.PendingLoginChallenge = capturedPendingChallenge;
            _logger.LogWarning(ex, "Error sending cancel-login to daemon for session {SessionId}; login was not cancelled", sessionId);
            throw;
        }

        // Reset auth state to allow a new login attempt
        session.AuthState = DaemonAuthState.NotAuthenticated;
        await NotifyAuthStateChangeAsync(session);

        _logger.LogInformation("Login cancelled for session {SessionId}, ready for new attempt", sessionId);
    }

    /// <summary>
    /// Attempts to log a RUNNING persistent session out in place via the daemon's <c>logout</c>
    /// command, without tearing the container down. IsPersistent-gated defense in depth, mirroring
    /// the other persistent-only operations in this class. On daemon success, the session is put
    /// through the SAME auth-state funnel every other login/logout transition uses
    /// (<see cref="NotifyAuthStateChangeAsync"/>) so subscribers see the account was forgotten, its
    /// cached resume challenge is cleared, and <see cref="DaemonSession.NeedsRelogin"/> is reset. On
    /// failure - the daemon reports failure, or the round-trip itself throws (socket error, timeout)
    /// - this performs NO teardown and returns a not-forgotten result; the caller
    /// (<see cref="Controllers.PersistentPrefillController"/> / the frontend) decides whether to fall
    /// back to a stop+restart. NOTE: an un-updated steam/epic daemon image reports SUCCESS here while
    /// only tearing down the live session, without deleting the stored account file - that case is
    /// in-band indistinguishable from a true success and is not detected by this method; it
    /// self-resolves once the daemon image is rebuilt with the account-file-delete fix.
    /// The pending login challenge / resume wait is cleared BEFORE the daemon round-trip, same
    /// ordering as <see cref="CancelLoginAsync"/> - a logout must not leave a stale challenge
    /// resumable while the (possibly slow) daemon call is in flight. Unlike
    /// <see cref="CancelLoginAsync"/>, that clear is NOT restored if the round-trip fails: logout
    /// intent is terminal, so there is nothing to resume even on failure.
    /// </summary>
    public async Task<PersistentLogoutResult> LogoutPersistentSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Session not found: {sessionId}");
        }

        if (!session.IsPersistent)
        {
            _logger.LogWarning("LogoutPersistentSessionAsync called for non-persistent session {SessionId}", sessionId);
            return new PersistentLogoutResult(false);
        }

        ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        LogoutOutcome outcome;
        try
        {
            outcome = await session.Client.LogoutWithReasonAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex,
                "Daemon logout command failed for persistent session {SessionId}; caller should fall back to a stop+restart",
                sessionId);
            return new PersistentLogoutResult(false);
        }

        if (!outcome.Success)
        {
            if (outcome.RequiresLogin)
            {
                // Older daemon image's pre-login command gate rejected "logout" outright because this
                // session hasn't finished authenticating - not a genuine failure. Still returns
                // forgotten=false; the caller (frontend) routes this case to cancelling the in-flight
                // login instead of falling back to a stop+restart.
                _logger.LogInformation(
                    "Daemon declined logout for persistent session {SessionId} before authentication completed " +
                    "(older daemon image); nothing to log out",
                    sessionId);
            }
            else
            {
                _logger.LogInformation(
                    "Daemon reported logout failed for persistent session {SessionId}; caller should fall back to a stop+restart",
                    sessionId);
            }
            return new PersistentLogoutResult(false);
        }

        session.AuthState = DaemonAuthState.NotAuthenticated;
        session.NeedsRelogin = false;
        await NotifyAuthStateChangeAsync(session);

        _logger.LogInformation("Session {SessionId} logged out in place; daemon forgot its stored account", sessionId);
        return new PersistentLogoutResult(true);
    }

    /// <summary>
    /// Removes this service's persistent auth volume when no session is currently running for it -
    /// used by the admin "clear all logins" endpoint (<c>POST .../persistent/clear-logins</c>) to
    /// forget a STOPPED service's stored login even though there is no live session/socket to send a
    /// <c>logout</c> command to. Never throws: Docker's "no such volume" (already gone) and "volume in
    /// use" (a stopped-but-still-attached container still references it) are both reported as typed
    /// results rather than propagated - the end state the caller cares about (no lingering login) is
    /// either already true, or requires the admin to remove the attached container first, which this
    /// method cannot force.
    /// </summary>
    // virtual: the escalation in ForgetRunningPersistentLoginAsync calls this, and unit tests override
    // it to exercise the hard-remove path without a live Docker daemon.
    public virtual async Task<PersistentVolumeClearResult> ClearPersistentAuthVolumeAsync(CancellationToken cancellationToken = default)
    {
        if (_dockerClient == null)
        {
            return PersistentVolumeClearResult.DockerUnavailable;
        }

        // RC5 (session 20260703-221336-2070027597): with deterministic naming a STOPPED
        // {ContainerPrefix}persistent container keeps this service's named auth volume ATTACHED, so the
        // force:false remove below 409s -> InUse and "clear stored logins" silently no-ops. Reap the
        // lingering stopped container first so the volume can actually be removed. A still-RUNNING
        // container is left untouched: the remove below then reports InUse, preserving the existing
        // refuse-while-running behavior instead of tearing down a live login.
        await RemoveStoppedPersistentContainersAsync(cancellationToken);

        var volumeName = GetPersistentConfigVolumeName();
        try
        {
            await _dockerClient.Volumes.RemoveAsync(volumeName, force: false, cancellationToken);
            _logger.LogInformation("Cleared persistent {ServiceName} auth volume {Volume}", ServiceName, volumeName);
            return PersistentVolumeClearResult.Removed;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Persistent {ServiceName} auth volume {Volume} does not exist; nothing to clear", ServiceName, volumeName);
            return PersistentVolumeClearResult.NotFound;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Persistent {ServiceName} auth volume {Volume} is still attached to a container; not removed", ServiceName, volumeName);
            return PersistentVolumeClearResult.InUse;
        }
    }

    /// <summary>
    /// Clears the login of the RUNNING persistent session for this service during the admin "clear
    /// stored logins" sweep, with a HARD guarantee that holds even against an un-updated daemon image.
    ///
    /// WHY the verify-then-escalate: the daemon's in-place <c>logout</c> command is not trustworthy on
    /// an old steam/epic image - it reports <c>Success=true</c> WITHOUT deleting the account file that
    /// lives in the container's named auth volume (documented on <see cref="LogoutPersistentSessionAsync"/>
    /// and the LogoutAsync endpoint), so the container keeps self-authenticating from that volume and
    /// the next login click just dismisses again. A soft logout alone therefore cannot make
    /// "clear stored logins" deterministic. This method:
    ///   1. tries the least-disruptive in-place logout first (on an UPDATED image this genuinely forgets
    ///      the account and the container keeps running);
    ///   2. VERIFIES it against the daemon's LIVE status - trusting the socket, not the logout's own
    ///      success flag - so the old-image "lied about success" case is caught;
    ///   3. only when the logout did not verifiably take (reported failure, still "logged-in", or the
    ///      status could not be read), ESCALATES to <see cref="TerminateSessionAsync"/> (which detaches
    ///      the named volume by removing the container) followed by <see cref="ClearPersistentAuthVolumeAsync"/>
    ///      (which deletes that volume outright, after reaping any lingering stopped container) - the
    ///      only image-version-independent hard remove.
    ///
    /// Terminating a persistent container is permitted here because this runs ONLY from an explicit
    /// admin action (clear-logins); background reapers still never terminate a persistent container.
    /// The escalated container is left stopped/removed with no stored login - exactly the clean state
    /// the admin is asking for. The soft-logout-verified path leaves the container running and merely
    /// logged out, unchanged from before.
    /// </summary>
    public async Task<PersistentRunningLoginClearOutcome> ForgetRunningPersistentLoginAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        // (1) Least-disruptive first: in-place logout.
        var logoutResult = await LogoutPersistentSessionAsync(sessionId, cancellationToken);

        // (2) Verify against the LIVE socket. Do NOT trust logoutResult.LoggedOut on its own: an old
        // image reports success while the volume token survives. Verified-clean == the daemon
        // acknowledged the logout AND no longer reports "logged-in".
        bool verifiedLoggedOut;
        try
        {
            var status = await GetSessionStatusAsync(sessionId, cancellationToken);
            verifiedLoggedOut = logoutResult.LoggedOut && status?.Status != "logged-in";
        }
        catch (Exception ex)
        {
            // Could not confirm the logout took effect - refuse to stand behind an unverified success;
            // escalate to the hard remove instead (mirrors the list endpoint's resilient status catch).
            _logger.LogInformation(ex,
                "Could not verify in-place logout for persistent session {SessionId}; escalating to hard remove",
                sessionId);
            verifiedLoggedOut = false;
        }

        if (verifiedLoggedOut)
        {
            _logger.LogInformation(
                "Persistent session {SessionId} logged out in place and verified clear; container left running",
                sessionId);
            return PersistentRunningLoginClearOutcome.LoggedOut;
        }

        // (3) Escalate: terminate so the named auth volume detaches, then delete the volume.
        // ClearPersistentAuthVolumeAsync also reaps a lingering stopped container first, so the delete
        // succeeds instead of returning InUse.
        _logger.LogWarning(
            "In-place logout did not verifiably clear persistent session {SessionId}; terminating the container " +
            "and deleting its named auth volume so the stored login cannot survive",
            sessionId);

        await TerminateSessionAsync(
            sessionId,
            reason: "Clear stored logins: hard-remove stale persistent login",
            force: true,
            terminatedBy: "admin");

        var volumeResult = await ClearPersistentAuthVolumeAsync(cancellationToken);
        return volumeResult is PersistentVolumeClearResult.Removed or PersistentVolumeClearResult.NotFound
            ? PersistentRunningLoginClearOutcome.HardRemoved
            : PersistentRunningLoginClearOutcome.HardRemoveFailed;
    }

    /// <summary>
    /// RC5 (session 20260703-221336-2070027597): force-removes any STOPPED
    /// <c>{ContainerPrefix}persistent</c> container that still holds this service's named auth volume
    /// attached, so <see cref="ClearPersistentAuthVolumeAsync"/> can actually delete the volume. A
    /// RUNNING container is deliberately skipped (the caller's <c>GetActivePersistentSession()==null</c>
    /// precondition means there is no live session, but a zombie could still be running - leave it so
    /// the volume remove reports InUse rather than killing it). Never throws: a failed list/remove is
    /// logged and swallowed, leaving the volume remove to report its own honest result.
    /// <paramref name="cancellationToken"/> bounds the docker calls. RemoveVolumes stays false - the
    /// named volume is removed separately by the caller, and this reaps only the container shell.
    /// </summary>
    private async Task RemoveStoppedPersistentContainersAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null)
        {
            return;
        }

        var containerName = $"{ContainerPrefix}persistent";
        IList<ContainerListResponse> matches;
        try
        {
            matches = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    All = true,
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        ["name"] = new Dictionary<string, bool> { [containerName] = true }
                    }
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to list persistent {ServiceName} containers while clearing auth volume; leaving the volume remove to report its own result",
                ServiceName);
            return;
        }

        // Docker's name filter is a substring match, so re-assert an EXACT name match (same shape as
        // ForceRemoveContainersByExactNameAsync) to avoid touching an unrelated container.
        foreach (var match in matches.Where(c => (c.Names ?? new List<string>()).Any(n => n.TrimStart('/') == containerName)))
        {
            if (string.Equals(match.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Persistent {ServiceName} container {Name} is still running; not removing it before clearing its auth volume",
                    ServiceName, containerName);
                continue;
            }

            if (await RemoveContainerForceAsync(match.ID, cancellationToken))
            {
                _logger.LogInformation(
                    "Removed stopped persistent {ServiceName} container {Id} so its auth volume can be cleared",
                    ServiceName, ShortContainerId(match.ID));
            }
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
            await _sessionService.CancelEntriesAsync(sessionId);

            // Broadcast history update if there was a current app
            if (!string.IsNullOrEmpty(session.CurrentAppId))
            {
                await BroadcastHistoryUpdatedAsync(sessionId, session.CurrentAppId, "Cancelled");
            }

            // Route through the single idempotent terminal funnel - the ONLY setter of
            // IsPrefilling=false. Idempotent, so a racing daemon "cancelled" socket event
            // cannot double-fire the terminal transition.
            await TransitionToTerminalAsync(session, PrefillState.Cancelled);

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
    /// Checks cache status by comparing cached depots against manifests.
    /// </summary>
    public virtual async Task<CacheStatusResult> GetCacheStatusAsync(
        string sessionId,
        List<string> appIds,
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

        var numericAppIds = appIds.Where(id => long.TryParse(id, out _)).Select(long.Parse);
        var cachedData = await _cacheService.GetCachedDepotsForAppsAsync(numericAppIds);
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
    public async Task SetSelectedAppsAsync(string sessionId, List<string> appIds, CancellationToken cancellationToken = default)
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

        // Start guard: reject a second prefill while one is already in flight (double-start /
        // SkipDownloads race). IsPrefilling is now driven by the terminal socket state, so this
        // check is reliable for the entire duration of the real download. Surfaced as HTTP 409.
        if (session.IsPrefilling)
        {
            throw new PrefillAlreadyRunningException($"A prefill is already in progress for session {sessionId}");
        }

        session.IsPrefilling = true;
        session.LastProgress = null;
        session.PreviousAppId = null;
        session.PreviousAppName = null;
        session.CurrentAppId = null;
        session.CurrentAppName = null;
        session.CurrentBytesDownloaded = 0;
        session.CurrentTotalBytes = 0;
        session.CompletedBytesTransferred = 0;
        session.TotalBytesTransferred = 0;
        await NotifyPrefillStartedAsync(session);
        var startDto = DaemonSessionDto.FromSession(session);
        await NotifyHubAsync(EventSessionUpdated, startDto);

        // Only Steam's daemon actually filters downloads by operating system; Epic/Xbox/BattleNet/Riot
        // silently ignore an OS filter today, so never forward one to them regardless of what any of
        // the 3 trigger paths (guest, persistent, scheduled) passed in.
        if (!ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(Platform))
        {
            operatingSystems = null;
        }

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

            PrefillResult result;
            try
            {
                result = await session.Client.PrefillAsync(all, recent, recentlyPurchased, top, force, operatingSystems, maxConcurrency, cachedDepots, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress", StringComparison.OrdinalIgnoreCase))
            {
                // The daemon rejected the command because it is already prefilling. A prefill IS
                // running, so do NOT flip IsPrefilling off / fire a terminal - surface as 409.
                throw new PrefillAlreadyRunningException(ex.Message);
            }

            // NOTE: Don't notify completion here - the daemon returns immediately with an acknowledgement.
            // The actual completion is detected via the socket progress terminal events.
            // The daemon ack only failing (without an exception) means the run never started, so
            // route through the single terminal funnel to clear IsPrefilling and emit Failed once.
            if (!result.Success)
            {
                await TransitionToTerminalAsync(session, PrefillState.Failed);
            }

            // Don't complete apps here - the daemon returns immediately with "Prefill started".
            // IsPrefilling stays TRUE from this ack through the real download; it is cleared ONLY
            // by the terminal funnel (TransitionToTerminalAsync) via a socket terminal event,
            // cancel, or socket disconnect. No `finally IsPrefilling=false` (that cleared it
            // milliseconds after the start ack, so it was never true during the real download).
            return result;
        }
        catch (PrefillAlreadyRunningException)
        {
            // Genuine "already running" - leave IsPrefilling true; just rethrow for the 409 mapping.
            throw;
        }
        catch
        {
            // Any other failure to dispatch the prefill means the run never started on the daemon.
            // Route through the single terminal funnel so IsPrefilling is cleared and exactly one
            // terminal PrefillStateChanged(Failed) is emitted, then rethrow for the controller.
            await TransitionToTerminalAsync(session, PrefillState.Failed);
            throw;
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
    /// Creates/starts a PERSISTENT admin daemon session. Reuses <see cref="CreateSessionAsync"/> with
    /// <c>isPersistent=true</c>, which (a) stamps <see cref="DaemonSession.IsPersistent"/>, (b) sets the
    /// expiry to the admin-configured validity window, and (c) mounts a stable named auth volume so the
    /// daemon's OWN login persists inside the container across restarts. The container starts RUNNING;
    /// the manager performs NO auto-login and NEVER injects/transfers a token. If the named volume
    /// already holds a valid login the daemon auto-authenticates ITSELF; otherwise the admin logs in
    /// interactively via the UI. The <paramref name="service"/> argument identifies the platform for the
    /// caller; this daemon instance is already service-specific.
    /// </summary>
    public async Task<DaemonSession> StartPersistentSessionAsync(
        PrefillPlatform service,
        Guid userId,
        string? ipAddress = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Starting persistent {Service} session for user {UserId}", service, userId);

        // The persistent container starts RUNNING. There is intentionally NO manager-side auto-login
        // here and the manager NEVER injects/transfers a token (that breaks Steam across servers).
        // The container mounts its own named auth volume: if that volume already holds a valid login
        // the daemon auto-authenticates ITSELF; otherwise the admin logs in interactively via the UI.
        // The daemon's live status is the source of truth.
        var session = await CreateSessionAsync(
            userId,
            ipAddress,
            userAgent,
            SessionType.Admin,
            isPersistent: true,
            cancellationToken: cancellationToken);

        return session;
    }

    /// <summary>
    /// Stops a PERSISTENT session's container via the existing teardown path. Because the session is
    /// persistent, <see cref="TerminateSessionAsync"/> tears the container down with RemoveVolumes=false,
    /// so the daemon's own named auth volume survives and a subsequent <see cref="StartPersistentSessionAsync"/>
    /// re-mounts the same login. No-op if the session is unknown.
    /// </summary>
    public Task StopPersistentSessionAsync(string sessionId, string? terminatedBy = null)
    {
        var session = GetSession(sessionId);
        if (session == null)
        {
            _logger.LogWarning("StopPersistentSessionAsync: session {SessionId} not found", sessionId);
            return Task.CompletedTask;
        }

        if (!session.IsPersistent)
        {
            _logger.LogWarning(
                "StopPersistentSessionAsync called for non-persistent session {SessionId}; its volume will be removed on teardown",
                sessionId);
        }

        return StopPersistentSessionCoreAsync(sessionId, terminatedBy);
    }

    /// <summary>
    /// Runs the actual teardown under <see cref="_persistentStartLock"/> so a Stop can never interleave
    /// with a concurrent persistent Start/adopt/replace for this service (Cursor #2) - e.g. a Stop
    /// racing a Start that is mid-adopt of the very container being stopped. Safe against the lock
    /// already being held by <c>CreateSessionCoreAsync</c>'s Error-state-replacement call into
    /// <see cref="TerminateSessionAsync"/>: that call happens on a DIFFERENT logical entry (the create
    /// path already holds the lock and never re-enters it), and <see cref="TerminateSessionAsync"/>
    /// itself never acquires <see cref="_persistentStartLock"/> - only the two public entry points
    /// (this one and the persistent branch of <c>CreateSessionAsync</c>) do, so there is no reentrant
    /// self-deadlock.
    /// </summary>
    private async Task StopPersistentSessionCoreAsync(string sessionId, string? terminatedBy)
    {
        await _persistentStartLock.WaitAsync();
        try
        {
            // Reuse the existing teardown; IsPersistent keeps RemoveVolumes=false so the daemon's auth survives.
            await TerminateSessionAsync(sessionId, "Persistent session stopped", force: false, terminatedBy: terminatedBy);
        }
        finally
        {
            _persistentStartLock.Release();
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
        if (!string.IsNullOrEmpty(session.CurrentAppId))
        {
            try
            {
                await _sessionService.CompleteEntryAsync(
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
        await _sessionService.CancelEntriesAsync(sessionId);

        // Persist termination to database
        await _sessionService.TerminateSessionAsync(sessionId, reason, terminatedBy);

        session.Status = DaemonSessionStatus.Terminated;
        session.EndedAt = DateTime.UtcNow;
        // RC3 (session 20260703-221336-2070027597): clear BOTH the manager-side resume cache and the
        // client's own queued/awaited challenge so a challenge the dying daemon already emitted can
        // never be replayed after this session is gone (paired with the dead-session guard in
        // OnCredentialChallengeAsync). Status is flipped to Terminated first so any challenge racing
        // in during teardown is rejected by that guard.
        ClearPendingLoginChallenge(session);
        session.Client.ClearPendingChallenges();

        // User policy: a persistent container's login exists ONLY while that container is alive -
        // stopping it must forget the daemon's stored account so a later Start never silently inherits
        // a previous life's login. Best-effort and bounded (see TryBestEffortLogoutAsync): a dead/hung
        // socket (e.g. the daemon already crashed) must never block this teardown. Covers every caller
        // of TerminateSessionAsync for a persistent session - explicit admin stop
        // (StopPersistentSessionAsync), Error-state replacement in the create path, and service
        // shutdown - since they all funnel through here.
        await TryBestEffortLogoutAsync(session, "session stop/teardown");

        // Broadcast session termination to all clients for real-time updates (both hubs)
        var terminatedEvent = new { sessionId = session.Id, reason = reason };
        await NotifyHubAsync(EventSessionTerminated, terminatedEvent);

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

                // Remove the (now stopped/killed) container. Containers are created with
                // AutoRemove=false, so a normal teardown that only stops/kills would leave the
                // container plus its token volume lingering until the next orphan sweep.
                //
                // RemoveVolumes is CONDITIONAL on persistence:
                //  - Non-persistent: RemoveVolumes=true. A login-required daemon (Xbox/Epic) stores
                //    its anonymous refresh token in an anonymous volume; wiping it ensures the auth
                //    does not survive the session (brief security requirement).
                //  - Persistent: RemoveVolumes=false. The auth lives on a STABLE NAMED volume mounted
                //    at create time; keeping it lets the daemon's OWN persisted login survive across
                //    container/manager restarts so the admin does not have to re-login every stop.
                // Force=true is a no-op once already stopped/killed.
                var removeVolumes = !session.IsPersistent;
                if (await RemoveContainerForceAsync(session.ContainerId, CancellationToken.None, removeVolumes))
                {
                    _logger.LogInformation(
                        "Removed container {ContainerId} (RemoveVolumes={RemoveVolumes}) for session {SessionId}",
                        session.ContainerId, removeVolumes, sessionId);
                }
                else
                {
                    // Already gone, or another teardown/orphan sweep is already removing this
                    // container - that's fine.
                    _logger.LogDebug("Container {ContainerId} removal already in progress or already gone", session.ContainerId);
                }
            }
            catch (DockerContainerNotFoundException)
            {
                // Container already removed (AutoRemove or a concurrent sweep)
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping/removing container {ContainerId}", session.ContainerId);
            }
        }

        // Notify subscribers
        await NotifySessionEndedAsync(session, reason);

        // Check if all daemons are now logged out after session removal
        if (!IsAnyDaemonAuthenticated())
        {
            FireAndForgetAsync(OnAllSessionsLoggedOutAsync, nameof(OnAllSessionsLoggedOutAsync));
        }

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
    /// Returns the single running persistent (admin, named-volume) session for this daemon, or
    /// <c>null</c> when none is up. Centralizes the "running persistent session" lookup so the
    /// scheduled-prefill reuse path and <c>PersistentPrefillController</c> resolve it identically.
    /// </summary>
    public DaemonSession? GetActivePersistentSession()
    {
        return _sessions.Values.FirstOrDefault(s => s.IsPersistent && s.Status == DaemonSessionStatus.Active);
    }

    /// <summary>
    /// Pure anchor for a persistent session's validity window: <paramref name="createdAtUtc"/> plus the
    /// admin-configured <paramref name="validityDays"/>. This is the exact formula used at session
    /// creation and preserved by the re-adopt path, so re-anchoring with the same validity is a no-op
    /// and changing it moves the date deterministically (never silently extends like now+validity would).
    /// </summary>
    public static DateTime ComputePersistentExpiry(DateTime createdAtUtc, int validityDays)
        => createdAtUtc.AddDays(validityDays);

    /// <summary>
    /// Resolves the concrete daemon singleton for a given platform. The single canonical copy of a
    /// switch that was previously duplicated verbatim in <c>PersistentPrefillController</c> and
    /// <c>ScheduledPrefillService</c> — both now call this instead. <c>default</c> is unreachable for
    /// any defined <see cref="PrefillPlatform"/> value; it stays defensive (returns null rather than
    /// throwing) to match the exact behavior of both prior copies.
    /// </summary>
    public static PrefillDaemonServiceBase? ResolveDaemon(IServiceProvider provider, PrefillPlatform platform)
    {
        switch (platform)
        {
            case PrefillPlatform.Steam:
                return provider.GetRequiredService<SteamDaemonService>();
            case PrefillPlatform.Epic:
                return provider.GetRequiredService<EpicPrefillDaemonService>();
            case PrefillPlatform.Xbox:
                return provider.GetRequiredService<XboxPrefillDaemonService>();
            case PrefillPlatform.BattleNet:
                return provider.GetRequiredService<BattleNetDaemonService>();
            case PrefillPlatform.Riot:
                return provider.GetRequiredService<RiotDaemonService>();
            default:
                return null;
        }
    }

    /// <summary>
    /// Resolves all platform daemon singletons. The single canonical copy of a generator previously
    /// duplicated verbatim in <c>PersistentPrefillController</c>. Enumerates <see cref="PrefillPlatform"/>
    /// itself rather than a hand-duplicated platform list, so a future platform added to the enum is
    /// picked up here automatically - only <see cref="ResolveDaemon"/>'s switch needs a matching case,
    /// since that is an unavoidable mapping to a concrete type.
    /// </summary>
    public static IEnumerable<PrefillDaemonServiceBase> ResolveAllDaemons(IServiceProvider provider)
    {
        foreach (var platform in Enum.GetValues<PrefillPlatform>())
        {
            var daemon = ResolveDaemon(provider, platform);
            if (daemon != null)
            {
                yield return daemon;
            }
        }
    }

    /// <summary>
    /// Re-anchors every RUNNING persistent session's <see cref="DaemonSession.ExpiresAt"/> to
    /// <c>createdAt + validityDays</c> (idempotent) and clears a stale <see cref="DaemonSession.NeedsRelogin"/>
    /// when the new window is in the future (the reaper re-flags it if it is already past). Also persists
    /// the new <c>ExpiresAtUtc</c> to each session's DB record so a manager restart re-adopts the new
    /// window rather than a stale longer one (the re-adopt path prefers a future <c>dbRecord.ExpiresAtUtc</c>).
    /// </summary>
    public async Task UpdatePersistentSessionExpiryAsync(int validityDays)
    {
        var now = DateTime.UtcNow;
        var targets = _sessions.Values
            .Where(s => s.IsPersistent && s.Status == DaemonSessionStatus.Active)
            .ToList();

        foreach (var session in targets)
        {
            session.ExpiresAt = ComputePersistentExpiry(session.CreatedAt, validityDays);
            if (session.ExpiresAt > now)
            {
                session.NeedsRelogin = false;
            }

            // Persist so the new window survives a restart (the re-adopt path prefers a future
            // dbRecord.ExpiresAtUtc; without this a shrink would leave the old longer value on disk).
            await _sessionService.UpdateExpiryAsync(session.Id, session.ExpiresAt);
        }
    }

    /// <summary>
    /// Gets sessions for a specific user
    /// </summary>
    public IEnumerable<DaemonSession> GetUserSessions(Guid userId)
    {
        return _sessions.Values.Where(s => s.UserId == userId).ToList();
    }

    /// <summary>
    /// Checks if any prefill daemon session is currently authenticated.
    /// Used by depot mapping service to detect when prefill is using the shared credentials.
    /// </summary>
    public bool IsAnyDaemonAuthenticated()
    {
        return _sessions.Values.Any(s =>
            s.Status == DaemonSessionStatus.Active &&
            s.AuthState == DaemonAuthState.Authenticated);
    }

    /// <summary>
    /// Terminates all active prefill sessions.
    /// Called when authentication is logged out.
    /// </summary>
    /// <param name="reason">Reason for termination (for logging)</param>
    /// <param name="includePersistent">
    /// When false (the default), persistent (system-owned) sessions are skipped: they are stopped only
    /// via the dedicated <c>PersistentPrefillController.StopAsync</c> path, so a credential logout (e.g.
    /// <c>SteamKit2Service.Authentication.LogoutAsync</c>'s PICS logout) never tears down the reused
    /// persistent container. Passing true bypasses that guard AND the per-service persistent start lock
    /// (<c>_persistentStartLock</c>), so only pass true from a caller that cannot race a persistent start
    /// (e.g. full service shutdown); otherwise stop persistent sessions via
    /// <c>PersistentPrefillController.StopAsync</c> / <c>StopPersistentSessionAsync</c> instead.
    /// </param>
    public async Task TerminateAllSessionsAsync(
        string reason = "Authentication logged out",
        bool includePersistent = false)
    {
        var sessions = _sessions.Values
            .Where(s => includePersistent || PrefillSessionService.IsTerminatableByAdmin(s))
            .ToList();
        var terminatedCount = 0;

        foreach (var session in sessions)
        {
            try
            {
                await TerminateSessionAsync(session.Id, reason, force: true, terminatedBy: "system");
                terminatedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to terminate session {SessionId} during auth logout", session.Id);
            }
        }

        if (terminatedCount > 0)
        {
            _logger.LogInformation("Terminated {Count} prefill sessions due to: {Reason}",
                terminatedCount, reason);
        }
    }

    /// <summary>
    /// Maximum number of SignalR connections allowed per session.
    /// Limits duplicate connections from page navigations/reconnects.
    /// </summary>
    private const int MaxConnectionsPerSession = 3;

    /// <summary>
    /// Adds a SignalR connection as a subscriber to session events.
    /// Limits connections per session to prevent duplicate event broadcasts
    /// from stale connections accumulating during page navigations.
    /// </summary>
    public void AddSubscriber(string sessionId, string connectionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            // If we're at the limit, remove oldest connections to make room
            // This prevents stale connections from accumulating during page navigations
            while (session.SubscribedConnections.Count >= MaxConnectionsPerSession)
            {
                var oldest = session.SubscribedConnections.First();
                session.SubscribedConnections.Remove(oldest);
                _logger.LogDebug("Removed stale subscriber {ConnectionId} from session {SessionId} (limit reached)",
                    oldest, sessionId);
            }

            session.SubscribedConnections.Add(connectionId);
            _logger.LogDebug("Added subscriber {ConnectionId} to session {SessionId} (total: {Count})",
                connectionId, sessionId, session.SubscribedConnections.Count);
        }
    }

    /// <summary>
    /// Replays the retained live progress snapshot to a SINGLE just-(re)subscribed connection,
    /// so a client that connects/refreshes/reconnects mid-prefill binds its bar immediately
    /// without waiting for the next periodic daemon tick. Reuses the existing
    /// <c>EventPrefillProgress</c> event (no new event) and sends ONLY to the caller's connection
    /// (no double-broadcast to already-subscribed clients). No-op when the session is not
    /// prefilling or has no retained snapshot yet.
    /// </summary>
    public async Task ReplayProgressAsync(string sessionId, string connectionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        // Snapshot the (IsPrefilling, LastProgress) pair into locals with single reads each so the
        // replay decision and payload are self-consistent even though these fields are written on
        // the daemon-event thread without synchronization. (V9)
        var isPrefilling = session.IsPrefilling;
        var snapshot = session.LastProgress;

        if (!isPrefilling || snapshot == null)
        {
            return;
        }

        try
        {
            await SendToClientAsync(connectionId, EventPrefillProgress,
                new { sessionId = session.Id, progress = snapshot });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to replay current prefill progress to connection {ConnectionId} for session {SessionId}",
                connectionId, sessionId);
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
                    "The {ServiceName} Prefill feature requires this image.",
                    imageName, ServiceName);
                throw;
            }
        }
    }

    /// <summary>
    /// Single per-tick pass over this daemon's in-memory sessions, called externally once a minute by
    /// <see cref="LancacheManager.Infrastructure.Services.PersistentSessionExpiryService"/> across all
    /// platforms (replaces the old per-instance <see cref="System.Threading.Timer"/>-driven
    /// <c>CleanupExpiredSessions</c>). Does three things, preserving exact prior behavior:
    /// (1) flags expired persistent sessions <see cref="DaemonSession.NeedsRelogin"/> and pushes a
    /// SignalR update - the container is NEVER torn down; (2) terminates expired non-persistent
    /// sessions; (3) fails stalled non-persistent prefills via the existing stall watchdog. Termination
    /// and stall-failure are properly awaited here (no longer fire-and-forget) since this is no longer
    /// constrained by a synchronous <see cref="System.Threading.TimerCallback"/> signature. Per-session
    /// work within each phase runs concurrently via <see cref="Task.WhenAll(IEnumerable{Task})"/>, and
    /// every unit of work is individually try/catch-isolated so one session's teardown exception can
    /// never abort processing of the rest of that tick's sessions - this restores the isolation the old
    /// fire-and-forget pattern gave for free, now that these calls are properly awaited instead.
    /// </summary>
    public async Task<PrefillSessionExpiryResult> ProcessSessionExpiryAsync(DateTime nowUtc)
    {
        var flaggedNeedsRelogin = 0;
        var terminated = 0;
        var stalledFailed = 0;

        var expiredSessions = _sessions.Values
            .Where(s => s.Status == DaemonSessionStatus.Active && PrefillSessionExpiryGates.IsExpired(s.ExpiresAt, nowUtc))
            .ToList();

        async Task ProcessExpiredSessionAsync(DaemonSession session)
        {
            try
            {
                if (PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, nowUtc))
                {
                    _logger.LogInformation(
                        "Persistent session past expiry: {SessionId}. Flagging for re-login (container left running).",
                        session.Id);
                    session.NeedsRelogin = true;
                    Interlocked.Increment(ref flaggedNeedsRelogin);

                    // Close the SignalR gap: the prior Timer-based reaper flipped this flag silently (a sync
                    // TimerCallback can't cleanly await a broadcast). Mirrors NotifyAuthStateChangeAsync's
                    // "mutate, then broadcast, wrapped in try/catch" shape so a broadcast failure never masks
                    // the state mutation that already happened above.
                    try
                    {
                        await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to broadcast session update after expiry flag for session {SessionId}",
                            session.Id);
                    }
                }
                else if (PrefillSessionExpiryGates.ShouldTerminate(session, nowUtc))
                {
                    _logger.LogInformation("Session expired: {SessionId}", session.Id);
                    await TerminateSessionAsync(session.Id, "Session expired");
                    Interlocked.Increment(ref terminated);
                }
            }
            catch (Exception ex)
            {
                // Per-session isolation: TerminateSessionAsync is now properly awaited (no longer
                // fire-and-forget), so without this try/catch one session's teardown exception would
                // propagate out of the Task.WhenAll below and abort processing of every other session
                // in this tick - including the stall watchdog phase that runs after this one.
                _logger.LogError(ex, "Error processing session expiry for session {SessionId}", session.Id);
            }
        }

        await Task.WhenAll(expiredSessions.Select(ProcessExpiredSessionAsync));

        // Stall watchdog: fail any actively-prefilling session that has transferred no new bytes for
        // longer than the configured threshold. Re-reads _sessions fresh (not the snapshot above) to
        // match prior behavior exactly, since termination above can remove entries mid-tick.
        var stallThreshold = TimeSpan.FromSeconds(GetStallTimeoutSeconds());
        var stalledSessions = _sessions.Values
            .Where(s => s.Status == DaemonSessionStatus.Active &&
                        !s.IsPersistent &&
                        (s.PrefillState == PrefillState.Started || s.PrefillState == PrefillState.Downloading) &&
                        IsPrefillStalled(s, nowUtc, stallThreshold))
            .ToList();

        async Task ProcessStalledSessionAsync(DaemonSession session)
        {
            try
            {
                _logger.LogWarning(
                    "Prefill stall detected for session {SessionId}: no new bytes for >{ThresholdSeconds}s. Failing the run.",
                    session.Id, stallThreshold.TotalSeconds);
                session.ErrorMessage = $"Prefill stalled: no bytes transferred for {(int)stallThreshold.TotalSeconds} seconds.";
                await FailStalledSessionAsync(session);
                Interlocked.Increment(ref stalledFailed);
            }
            catch (Exception ex)
            {
                // Same per-session isolation guarantee as the expiry phase above - one stalled
                // session's teardown failure must not stop the rest from being failed this tick.
                _logger.LogError(ex, "Error failing stalled session {SessionId}", session.Id);
            }
        }

        await Task.WhenAll(stalledSessions.Select(ProcessStalledSessionAsync));

        return new PrefillSessionExpiryResult(flaggedNeedsRelogin, terminated, stalledFailed);
    }

    /// <summary>
    /// Fails a stalled prefill: best-effort tells the daemon to stop downloading (so it does not keep
    /// transferring bytes after we have given up on the run), then routes through the single
    /// idempotent terminal funnel with <see cref="PrefillState.Failed"/>. The daemon cancel is sent
    /// directly to the client rather than via <see cref="CancelPrefillAsync"/> so the terminal state
    /// stays <c>Failed</c> (a stall is a failure, not a user cancellation).
    /// </summary>
    private async Task FailStalledSessionAsync(DaemonSession session)
    {
        try
        {
            await session.Client.CancelPrefillAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Best-effort cancel of stalled prefill failed for session {SessionId}; failing the run anyway",
                session.Id);
        }

        await TransitionToTerminalAsync(session, PrefillState.Failed);
    }

    /// <summary>
    /// Returns true when a session is actively prefilling but has transferred no new bytes
    /// for longer than <paramref name="stallThreshold"/>. Pure function — no side effects.
    /// </summary>
    internal static bool IsPrefillStalled(DaemonSession session, DateTime nowUtc, TimeSpan stallThreshold)
    {
        if (!session.IsPrefilling)
        {
            return false;
        }

        // Volatile read pairs with the Volatile.Write on the progress/terminal threads so this
        // timer thread never observes a torn or stale tick value. 0 = no prefill clock set.
        var lastProgressTicks = Volatile.Read(ref session.LastProgressTicksUtc);
        if (lastProgressTicks == 0L)
        {
            return false;
        }

        return nowUtc - new DateTime(lastProgressTicks, DateTimeKind.Utc) > stallThreshold;
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
        var useTcp = _networkOptions.CurrentValue.UseTcp;
        if (useTcp.HasValue)
        {
            return useTcp.Value;
        }

        return OperatingSystemDetector.IsWindows;
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

        using var listener = new TcpListener(IPAddress.Loopback, 0);
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

        if (OperatingSystemDetector.IsWindows)
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

    private int GetSessionTimeoutMinutes()
    {
        return _configuration.GetValue<int>("Prefill:SessionTimeoutMinutes", DefaultSessionTimeoutMinutes);
    }

    private int GetStallTimeoutSeconds()
    {
        return _configuration.GetValue<int>("Prefill:StallTimeoutSeconds", DefaultStallTimeoutSeconds);
    }

    /// <summary>
    /// Determines if host network mode should be used based on lancache-dns configuration.
    /// Returns true if lancache-dns uses host networking or if explicitly configured.
    /// </summary>
    private async Task<bool> ShouldUseHostNetworkingAsync(CancellationToken cancellationToken = default)
    {
        // Check explicit configuration first
        var networkMode = _networkOptions.CurrentValue.NetworkMode;
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
        var networkMode = _networkOptions.CurrentValue.NetworkMode;
        if (string.Equals(networkMode, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return networkMode;
    }

    public void Dispose()
    {
        if (_disposed) return;

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
/// Result of one <see cref="PrefillDaemonServiceBase.ProcessSessionExpiryAsync"/> pass, for
/// structured logging/diagnostics at the caller (<c>PersistentSessionExpiryService</c>).
/// </summary>
public readonly record struct PrefillSessionExpiryResult(int FlaggedNeedsRelogin, int Terminated, int StalledFailed);

/// <summary>
/// Result of <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync"/>. <see cref="LoggedOut"/>
/// is true when the daemon acknowledged the logout; false covers a genuine failure (a failed response
/// or the round-trip throwing), which the caller must fall back on with a stop+restart rather than
/// assuming the account was forgotten. Note this is NOT a reliable "was the account actually deleted"
/// signal: an un-updated steam/epic daemon image also reports success while only tearing down the
/// live session, not deleting the stored account file - that case is in-band indistinguishable and
/// self-resolves once the image is rebuilt.
/// </summary>
public readonly record struct PersistentLogoutResult(bool LoggedOut);

/// <summary>
/// Result of <see cref="PrefillDaemonServiceBase.ClearPersistentAuthVolumeAsync"/>.
/// </summary>
public enum PersistentVolumeClearResult
{
    /// <summary>The volume was found and removed.</summary>
    Removed,

    /// <summary>The volume did not exist - already gone, nothing to clear.</summary>
    NotFound,

    /// <summary>The volume still exists but is attached to a container and could not be removed.</summary>
    InUse,

    /// <summary>Docker is not available.</summary>
    DockerUnavailable
}

/// <summary>
/// Outcome of <see cref="PrefillDaemonServiceBase.ForgetRunningPersistentLoginAsync"/>: how a RUNNING
/// persistent container's stored login was cleared during the admin "clear stored logins" sweep.
/// </summary>
public enum PersistentRunningLoginClearOutcome
{
    /// <summary>
    /// The daemon's in-place logout was verified against its live status (no longer "logged-in"); the
    /// container is still running and merely logged out. Least disruptive - only reached on a daemon
    /// image whose logout genuinely forgets the account.
    /// </summary>
    LoggedOut,

    /// <summary>
    /// The soft logout did not verifiably forget the login (an old image reported success while its
    /// named-volume token survived, the daemon reported failure, or the live status could not be read),
    /// so the container was terminated and its named auth volume deleted - a hard, image-version-
    /// independent remove. The container ends up stopped with no stored login.
    /// </summary>
    HardRemoved,

    /// <summary>
    /// Escalation ran (container terminated) but the named auth volume could not be deleted for a real
    /// reason (still attached, or Docker unavailable), so the login may survive. The honest failure the
    /// caller must surface rather than reporting a false success.
    /// </summary>
    HardRemoveFailed
}
