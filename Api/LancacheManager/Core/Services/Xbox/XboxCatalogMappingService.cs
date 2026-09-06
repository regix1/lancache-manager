using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.Xbox;

/// <summary>
/// Scheduled Xbox / Microsoft Store catalog mapping service. Mirrors the IDEA of
/// <see cref="LancacheManager.Core.Services.EpicMapping.EpicMappingService"/>'s scheduling partial:
/// a runtime-configurable background schedule (NOT a user prefill) keeps the Xbox name source
/// (<c>XboxCdnPatterns</c> / <c>XboxGameMappings</c>) populated, so log-driven naming, detection,
/// removal, and banners light up for ANY owned game - including one downloaded on a real Xbox/PC
/// that was never prefilled.
///
/// WHY THE CATALOG SOURCE DIFFERS FROM EPIC (documented decision): Epic reaches its catalog from the
/// manager via a stored OAuth refresh token + a direct HTTP client (<c>EpicApiDirectClient</c>), so its
/// scheduled refresh calls <c>GetOwnedGamesAsync</c>/<c>GetCdnInfoAsync</c> with no daemon. Xbox's
/// owned-titles list comes from the account-scoped titlehub API (MSA -&gt; XSTS) and the per-package CDN
/// fragments come from resolving each title's package manifest - that protocol lives in the Xbox prefill
/// daemon, and the manager holds no Xbox credential of its own. So the scheduled collection re-reads the
/// daemon's ALREADY-authenticated session via <c>get-cdn-info</c> (the daemon auto-manages its own MSA
/// token, so there is NO per-run interactive login). The decoupling that matters is achieved: collection
/// is driven by THIS schedule + a manual trigger + an opportunistic refresh when a session authenticates,
/// never by a user starting a prefill download. Storage/naming are REUSED unchanged
/// (<see cref="XboxMappingService.MergeDaemonCatalogAsync"/> + <see cref="XboxMappingService.ResolveDownloadsAsync"/>).
/// </summary>
public partial class XboxCatalogMappingService : ConfigurableScheduledService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly XboxMappingService _mappingService;

    // Manager-side daemon-free login dependencies (see XboxCatalogMappingService.Authentication.cs).
    private readonly XboxAuthClient _authClient;
    private readonly XboxAuthStorageService _authStorage;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;

    private bool _refreshShowNotification = true;
    private MappingOperationReporter? _currentMappingReporter;

    // Serializes every Xbox catalog-mapping producer: the scheduled tick, manual refresh, and
    // post-authentication harvest. Besides avoiding duplicate catalog work, this preserves the
    // frontend's one-card/one-operation recovery contract for OperationType.XboxMapping.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Service-lifetime token: links the fire-and-forget on-authentication refresh to shutdown so a
    // catalog poll in flight is cancelled cleanly when the host stops (instead of CancellationToken.None,
    // which would keep polling the daemon during teardown).
    private readonly CancellationTokenSource _shutdownCts = new();

    private XboxPrefillDaemonService? _daemonService;
    private bool _disposed;

    // Optional (like ServiceScheduleRegistry's _tracker) so unit tests constructing the service directly
    // keep compiling; at runtime DI supplies the singleton. Mirrors the Xbox integration's authenticated
    // state into the unified activity registry so the Xbox integration status dot reads the one
    // ActivityUpdated event.
    private readonly IActivityRegistry? _activityRegistry;

    /// <summary>Schedule key the registry uses to surface this on the unified Schedules page.</summary>
    public string ScheduleServiceKey => "xboxMapping";

    protected override string ServiceName => "XboxCatalogMappingService";

    // This schedule surfaces run notifications the user can gate from the Schedules page (mirrors Epic).
    protected override bool SupportsNotifications => true;

    // Routine background chore: scheduled runs stay quiet by default, manually triggered runs still
    // notify. Same default as the Epic and Steam mapping schedules so all three behave alike. A run
    // that collects nothing still reports itself skipped either way - this only decides whether the
    // card surfaces without being asked for.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    public XboxCatalogMappingService(
        ILogger<XboxCatalogMappingService> logger,
        IServiceScopeFactory scopeFactory,
        XboxMappingService mappingService,
        XboxAuthClient authClient,
        XboxAuthStorageService authStorage,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        IStateService stateService,
        IActivityRegistry? activityRegistry = null)
        : base(logger, TimeSpan.FromHours(12)) // Default: 12h refresh interval (mirrors EpicMappingService)
    {
        _scopeFactory = scopeFactory;
        _mappingService = mappingService;
        _authClient = authClient;
        _authStorage = authStorage;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _activityRegistry = activityRegistry;

        // Apply user-saved interval + run-on-startup overrides before the loop starts (mirrors Epic).
        LoadStateOverrides(stateService, ScheduleServiceKey);
    }

    /// <summary>
    /// Sets the authenticated flag and mirrors the Xbox integration's authenticated state into the unified
    /// activity registry (best-effort) so the Xbox integration status dot reads the one ActivityUpdated event.
    /// </summary>
    private void SetIsAuthenticated(bool value)
    {
        _isAuthenticated = value;
        _ = _activityRegistry?.ReportAsync(ActivityDomains.Integration, "xbox", ActivityAspects.Authenticated, value);
    }

    protected override Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Opportunistically refresh the moment a daemon session authenticates, so a fresh login
        // populates names promptly instead of waiting for the next scheduled tick. This REPLACES the
        // old prefill->mapping coupling: the daemon no longer calls the mapping service; the scheduled
        // service LISTENS for auth and schedules itself. Mirrors EpicMappingService.SubscribeDaemonEvents.
        SubscribeDaemonEvents();

        // Manager-side daemon-free login: silently refresh saved MSA credentials on startup (mirrors
        // EpicMappingService.InitializeAsync). No daemon, no browser - just a token refresh that lights up
        // the auth-status surface and lets the next scheduled tick harvest the catalog.
        if (_authStorage.HasSavedCredentials())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), _shutdownCts.Token);
                    await TryAutoReconnectAsync();
                }
                catch (OperationCanceledException)
                {
                    // Host shutting down - nothing to do.
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Xbox mapping startup auto-reconnect failed");
                }
            }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    protected override Task CleanupAsync(CancellationToken cancellationToken)
    {
        // Cancel any in-flight on-authentication refresh before we drop the daemon subscription.
        if (!_shutdownCts.IsCancellationRequested)
        {
            _shutdownCts.Cancel();
        }
        UnsubscribeDaemonEvents();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resolves the singleton daemon. Prefers the reference captured at subscribe time; otherwise
    /// resolves lazily via a scope (mirrors EpicMappingService) to avoid a constructor-time dependency
    /// between two hosted singletons. The scope is disposed immediately - the resolved instance is a
    /// root-owned singleton and outlives the scope.
    /// </summary>
    private XboxPrefillDaemonService? ResolveDaemonService()
    {
        if (_daemonService != null)
        {
            return _daemonService;
        }

        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetService<XboxPrefillDaemonService>();
    }

    private void SubscribeDaemonEvents()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            _daemonService = scope.ServiceProvider.GetService<XboxPrefillDaemonService>();
            if (_daemonService != null)
            {
                _daemonService.OnDaemonAuthenticated += HandleDaemonAuthenticated;
                _logger.LogInformation("Subscribed to Xbox daemon auth state change events for catalog mapping");
            }
            else
            {
                _logger.LogDebug("XboxPrefillDaemonService not available - daemon event subscriptions skipped");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Xbox daemon events");
        }
    }

    private void UnsubscribeDaemonEvents()
    {
        if (_daemonService != null)
        {
            _daemonService.OnDaemonAuthenticated -= HandleDaemonAuthenticated;
            _daemonService = null;
            _logger.LogDebug("Unsubscribed from Xbox daemon auth state change events");
        }
    }

    /// <summary>
    /// On daemon authentication, refresh the catalog from the newly authenticated session. Routed
    /// through the serialized <see cref="RefreshNowAsync"/> so it never collides with a scheduled tick.
    /// Decoupled: the schedule owns mapping; the auth event only nudges it (no prefill download needed).
    /// </summary>
    private Task HandleDaemonAuthenticated()
    {
        if (_disposed || _shutdownCts.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        // Capture the service-lifetime token before going fire-and-forget so the refresh stops cleanly
        // if the host shuts down mid-poll (replaces the old CancellationToken.None).
        var token = _shutdownCts.Token;
        _logger.LogInformation("Xbox daemon authenticated - refreshing catalog from the new session");
        _ = Task.Run(async () =>
        {
            try
            {
                // A login the user just completed is a user action, so this pass carries the manual
                // trigger rather than whatever the last scheduled tick happened to leave behind.
                await RefreshNowAsync(token, RunTrigger.Manual);
            }
            catch (OperationCanceledException)
            {
                // Service is shutting down - nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "On-authentication Xbox catalog refresh failed");
            }
        });
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            base.Dispose();
            return;
        }
        _disposed = true;

        if (!_shutdownCts.IsCancellationRequested)
        {
            _shutdownCts.Cancel();
        }
        UnsubscribeDaemonEvents();
        _refreshGate.Dispose();
        _loginStartLock.Dispose();
        _shutdownCts.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Result of a single Xbox catalog refresh pass: CDN patterns newly persisted + downloads re-tagged.
/// </summary>
public class XboxCatalogRefreshResult
{
    public int NewPatterns { get; set; }
    public int Resolved { get; set; }
}
