using System.Collections.Concurrent;
using LancacheManager.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Core.Interfaces;
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
    internal static GuestPrefillGate GuestGate { get; } = new();
    private readonly object _terminationSync = new();
    private readonly Dictionary<string, SessionTermination> _terminations = new();

    /// <summary>
    /// Seam over the Docker Engine operations this service performs. Each daemon owns its own instance
    /// (created from the injected factory) and disposes it, matching the previous one-client-per-service
    /// model. Not connected until <see cref="StartAsync"/> probes Docker; <see cref="IPrefillContainerGateway.IsAvailable"/>
    /// replaces the previous <c>_dockerClient != null</c> availability check.
    /// </summary>
    protected readonly IPrefillContainerGateway _containerGateway;
    private bool _disposed;

    // Set true at the top of StopAsync so a create completing after shutdown began is rejected at its
    // final registration step instead of escaping the one-time _sessions snapshot.
    private volatile bool _stopping;
    protected readonly bool _isRunningInContainer;
    private readonly IOptionsMonitor<PrefillNetworkOptions> _networkOptions;

    /// <summary>
    /// Shared lancache locator (config -&gt; env-file/docker-inspect -&gt; heartbeat-verified
    /// auto-detection). Owns cache-IP resolution and the lancache-dns container bridge-IP lookup that
    /// this class used to duplicate; the same singleton also backs the Status Check feature.
    /// </summary>
    private readonly ILancacheServerLocator _locator;

    // Optional (like ServiceScheduleRegistry's _tracker) so unit tests that construct a derived daemon
    // directly keep compiling; at runtime DI always supplies the singleton. Each session lifecycle
    // transition mirrors the session's presence, this platform's persistent-container state, and (for the
    // anonymous Battle.net/Riot daemons) integration connectivity into the unified activity registry so
    // the Prefill Sessions, persistent-container and integration status dots read the one ActivityUpdated event.
    private readonly IActivityRegistry? _activityRegistry;

    // Optional for the same reason as _activityRegistry above: unit tests construct a derived daemon
    // directly and must keep compiling, while at runtime DI always supplies the singleton. An
    // interactive login registers one operation here so its notification card carries a real id and
    // its X reaches CancelLoginAsync. A daemon built without a tracker simply raises no card.
    private readonly IUnifiedOperationTracker? _operationTracker;

    /// <summary>
    /// The lancache server IP most recently injected into a daemon container via the
    /// <c>LANCACHE_IP</c> env var, plus the <see cref="LancacheServerLocation.Source"/> it was located
    /// through (config | dns | dockerInspect | envFile | detected | none). Set during container creation
    /// from <see cref="ILancacheServerLocator.LocateAsync(bool, System.Threading.CancellationToken)"/>
    /// and read by the diagnostics builder to surface on the frontend. IP is null when no cache IP
    /// could be determined.
    /// </summary>
    private string? _lastInjectedLancacheIp;
    private string? _lastLancacheIpSource;

    /// <summary>
    /// Read-only view of the most recent LANCACHE_IP injection for this service, surfaced on the
    /// admin sessions endpoint. Null until the first daemon container is created this process
    /// lifetime.
    /// </summary>
    public string? LastInjectedLancacheIp => _lastInjectedLancacheIp;
    public string? LastLancacheIpSource => _lastLancacheIpSource;

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
    internal PersistentPrefillEditSessionGate PersistentEditSessionGate { get; } = new();

    // Configuration defaults
    private const int DefaultSessionTimeoutMinutes = 120;
    private const int DefaultStallTimeoutSeconds = 180;
    private const int DefaultAbandonedLoginTimeoutSeconds = 900;
    private const int DefaultTcpPort = 45555;

    // Bounded wait for a session's in-flight daemon event callbacks to finish during teardown (detach or
    // terminate) before its client is disposed, so no late status/progress event writes a DB row or
    // broadcasts. Best-effort: a callback slower than this can still run afterwards (the handlers re-check
    // liveness before durable writes), but shutdown is never blocked longer than this.
    private static readonly TimeSpan _eventDrainTimeout = TimeSpan.FromSeconds(5);

    // Bounded wait for a container removal on a shutdown/rejected-create path, so an unresponsive Docker
    // call can never block shutdown (never CancellationToken.None on those paths).
    private static readonly TimeSpan _containerTeardownTimeout = TimeSpan.FromSeconds(10);

    // How long an interactive login waits for a session's login lock before giving up. The automatic
    // startup login holds that lock for its whole attempt, so failing the instant it is held rejects a
    // user who clicked inside a window they cannot see. The wait is BOUNDED because the caller is an HTTP
    // request that still has a full login round-trip ahead of it once the lock is acquired: waiting
    // longer would trade a fast, actionable "already in progress" answer for a request that looks hung.
    // Kept short deliberately. It exists to absorb a brief overlap, not to outlast a full login: those
    // run to their own 30s command timeout, so a longer wait would only delay the same refusal behind a
    // spinner.
    private static readonly TimeSpan _loginLockWaitTimeout = TimeSpan.FromSeconds(3);

    // Docker labels stamped onto PERSISTENT daemon containers so they can survive a manager restart
    // and be re-adopted (reconnected) instead of being force-removed by the orphan cleanup sweep.
    private const string PersistentLabelKey = "lancache.prefill.persistent";
    private const string ServiceLabelKey = "lancache.prefill.service";
    private const string SessionIdLabelKey = "lancache.prefill.sessionId";
    private const string UserIdLabelKey = "lancache.prefill.userId";

    /// <summary>
    /// Indicates whether Docker is available and connected.
    /// </summary>
    public bool IsDockerAvailable => _containerGateway.IsAvailable;

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
    /// Raised IN-PROCESS on every LIVE prefill progress tick, after the session snapshot has been
    /// updated and the existing SignalR fan-out has run. It carries the same normalized progress the
    /// clients receive, plus the session's monotonic <see cref="DaemonSession.ProgressSequence"/>.
    ///
    /// This exists so a server-side consumer - the scheduled prefill run - can react to the daemon's
    /// PUSH instead of sampling <see cref="DaemonSession.LastProgress"/> on a timer. The push path
    /// already existed for clients; the scheduler was the one consumer polling in the middle of it.
    ///
    /// TERMINAL states are deliberately NOT raised here: they flow through TransitionToTerminalAsync.
    /// Handlers must be cheap; they are awaited before the next progress tick is processed and their
    /// exceptions are caught so a bad subscriber can never break the daemon's own progress handling.
    /// </summary>
    public event Func<DaemonSession, PrefillProgress, long, Task>? PrefillProgressUpdated;

    /// <summary>
    /// Invokes each <see cref="PrefillProgressUpdated"/> subscriber in isolation.
    ///
    /// The invocation list is snapshotted first, so a concurrent unsubscribe cannot mutate it
    /// mid-iteration, and each handler is awaited and caught individually - awaiting a multicast
    /// Func&lt;...,Task&gt; directly would only observe the LAST handler's task and let one failure
    /// swallow the rest.
    /// </summary>
    private async Task RaisePrefillProgressAsync(DaemonSession session, PrefillProgress progress, long sequence)
    {
        var handlers = PrefillProgressUpdated;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                await ((Func<DaemonSession, PrefillProgress, long, Task>)handler)(session, progress, sequence);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PrefillProgressUpdated handler for session {SessionId}", session.Id);
            }
        }
    }

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
        ILancacheServerLocator locator,
        IPrefillContainerGatewayFactory containerGatewayFactory,
        IActivityRegistry? activityRegistry = null,
        IUnifiedOperationTracker? operationTracker = null)
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
        _containerGateway = containerGatewayFactory.Create();
        _isRunningInContainer = bool.TryParse(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), out var inContainer) && inContainer;
        _activityRegistry = activityRegistry;
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// Maximum number of SignalR connections allowed per session.
    /// Limits duplicate connections from page navigations/reconnects.
    /// </summary>
    private const int MaxConnectionsPerSession = 3;

    private string? _cachedHostDataPath;

    public void Dispose()
    {
        if (_disposed) return;

        _containerGateway.Dispose();

        foreach (var session in _sessions.Values)
        {
            session.Client.Dispose();
            session.CancellationTokenSource.Dispose();
        }

        _disposed = true;
    }
}
