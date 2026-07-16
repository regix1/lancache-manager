using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Services.Xbox;

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

    // Display flag + highest percent for the refresh currently in flight. RefreshNowAsync is serialized
    // by _refreshGate, so a single run owns these at a time; the terminal-emit closure reads them so the
    // final event carries the run's stable visibility and does not regress the bar.
    private bool _refreshShowNotification = true;
    private double _refreshHighestPercent;

    // Serializes the scheduled tick, the manual refresh endpoint, and the on-authentication nudge so
    // concurrent triggers never double-poll the daemon's get-cdn-info at the same time.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    // Service-lifetime token: links the fire-and-forget on-authentication refresh to shutdown so a
    // catalog poll in flight is cancelled cleanly when the host stops (instead of CancellationToken.None,
    // which would keep polling the daemon during teardown).
    private readonly CancellationTokenSource _shutdownCts = new();

    private XboxPrefillDaemonService? _daemonService;
    private bool _disposed;

    /// <summary>Schedule key the registry uses to surface this on the unified Schedules page.</summary>
    public string ScheduleServiceKey => "xboxMapping";

    protected override string ServiceName => "XboxCatalogMappingService";

    // This schedule surfaces run notifications the user can gate from the Schedules page (mirrors Epic).
    // The base default mode (All) applies unless the user overrides it.
    protected override bool SupportsNotifications => true;

    public XboxCatalogMappingService(
        ILogger<XboxCatalogMappingService> logger,
        IServiceScopeFactory scopeFactory,
        XboxMappingService mappingService,
        XboxAuthClient authClient,
        XboxAuthStorageService authStorage,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        IStateService stateService)
        : base(logger, TimeSpan.FromHours(12)) // Default: 12h refresh interval (mirrors EpicMappingService)
    {
        _scopeFactory = scopeFactory;
        _mappingService = mappingService;
        _authClient = authClient;
        _authStorage = authStorage;
        _notifications = notifications;
        _operationTracker = operationTracker;

        // Apply user-saved interval + run-on-startup overrides before the loop starts (mirrors Epic).
        LoadStateOverrides(stateService, ScheduleServiceKey);
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
    /// Scheduled tick: collect the catalog from authenticated daemon sessions and resolve downloads.
    /// </summary>
    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await RefreshNowAsync(stoppingToken);
    }

    /// <summary>
    /// Runs one catalog collection + download-resolution pass. Shared by the scheduled tick, the manual
    /// refresh endpoint, and the on-authentication nudge. Serialized via <c>_refreshGate</c>. Best-effort:
    /// with no authenticated daemon session it simply collects nothing and returns zeroes.
    /// </summary>
    public async Task<XboxCatalogRefreshResult> RefreshNowAsync(CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            // Resolve the display flag once per run from the trigger that started it (Manual/Startup/
            // Scheduled) so it stays stable across the whole refresh. Lifecycle events are ALWAYS
            // emitted; the frontend gates the card on this flag (display-flag pattern, not transport
            // suppression). The tracker owns the CTS and is its single disposer once registered.
            _refreshShowNotification = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
            _refreshHighestPercent = 0;

            var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // Assigned by RegisterOperation below; the terminal-emit closure captures the variable and
            // runs later (inside CompleteOperation), so it observes the real id.
            var operationId = Guid.Empty;
            operationId = _operationTracker.RegisterOperation(
                OperationType.XboxMapping,
                "Xbox Catalog Refresh",
                refreshCts,
                onTerminalEmit: info => EmitRefreshTerminalAsync(info, operationId));

            var token = refreshCts.Token;
            var success = false;
            string? error = null;
            try
            {
                // Four equal-weight phases (manager harvest, daemon harvest, download resolve, banner
                // backfill). The 0% Started is awaited before any phase so a slow Started send cannot
                // land after the first progress tick.
                await EmitRefreshProgressAsync(operationId, 0, "signalr.xboxMapping.starting");

                var newPatterns = 0;

                // Source 1: the manager-side daemon-free login (Epic-style). Refreshes the saved MSA token,
                // re-harvests titlehub titles + packagespc CDN fragments, and merges them. No daemon needed.
                if (IsAuthenticated)
                {
                    newPatterns += await HarvestManagerCatalogAsync(token);
                }

                await EmitRefreshProgressAsync(operationId, 25, "signalr.xbox.mapping.collecting");

                // Source 2: authenticated prefill daemon session(s). PRESERVED so prefill still feeds the SAME
                // shared library (the Epic two-source model) - a prefill login contributes its owned catalog too.
                var daemon = ResolveDaemonService();
                if (daemon != null)
                {
                    newPatterns += await daemon.RefreshCatalogFromActiveSessionsAsync(token);
                }
                else
                {
                    _logger.LogDebug("XboxPrefillDaemonService unavailable - skipping the daemon catalog source");
                }

                await EmitRefreshProgressAsync(operationId, 50, "signalr.xbox.mapping.collecting");

                var resolved = await _mappingService.ResolveDownloadsAsync(token);

                await EmitRefreshProgressAsync(operationId, 75, "signalr.xboxMapping.starting");

                // Self-heal art-less mappings: retry the DisplayCatalog banner for any title whose first
                // fetch hiccupped transiently (ResolveDownloadsAsync only fetches art for products resolved
                // in that pass). Catalog-refresh path only (bounded 12h cadence), best-effort - a failure
                // here must never fail the whole refresh.
                try
                {
                    await _mappingService.BackfillMissingBannerArtAsync(token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Xbox banner-art backfill failed during catalog refresh");
                }

                _logger.LogInformation(
                    "Xbox catalog refresh: {NewPatterns} new CDN pattern(s), {Resolved} download(s) re-tagged",
                    newPatterns, resolved);

                success = true;
                return new XboxCatalogRefreshResult { NewPatterns = newPatterns, Resolved = resolved };
            }
            catch (OperationCanceledException)
            {
                // Mark the tracked op cancelled so the single terminal event is a cancellation, not a failure.
                _operationTracker.CancelOperation(operationId);
                throw;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                throw;
            }
            finally
            {
                // Single terminal, emitted exactly once from the onTerminalEmit gate (CompletedFlag-gated),
                // even when the run threw. The tracker disposes the run's CTS here.
                _operationTracker.CompleteOperation(operationId, success, error);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// Emits a running progress tick for the in-flight refresh over the existing
    /// <see cref="SignalREvents.XboxMappingProgress"/> event (the frontend handler is status-aware).
    /// Percent is clamped monotonic so the bar never regresses. Best-effort: a SignalR send failure
    /// must never fault the refresh (it can run from the fire-and-forget on-authentication nudge).
    /// </summary>
    private async Task EmitRefreshProgressAsync(Guid operationId, double percent, string stageKey)
    {
        if (percent > _refreshHighestPercent)
        {
            _refreshHighestPercent = percent;
        }

        _operationTracker.UpdateProgress(operationId, _refreshHighestPercent, stageKey);

        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.XboxMappingProgress, new
            {
                operationId,
                success = false,
                // OperationStatus serializes lowercase ("running") via its converter, matching the
                // frontend XboxMappingProgressEvent.status contract.
                status = OperationStatus.Running,
                stageKey,
                percentComplete = _refreshHighestPercent,
                gamesDiscovered = _gamesDiscovered,
                cancelled = false,
                error = (string?)null,
                context = new Dictionary<string, object?>(),
                message = string.Empty,
                isTerminal = false,
                showNotification = _refreshShowNotification
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit Xbox scheduled refresh progress ({StageKey})", stageKey);
        }
    }

    /// <summary>
    /// The single terminal emit for a scheduled/manual refresh, invoked exactly once by the tracker.
    /// Success carries 100; failure carries the highest percent reached. A cancel is surfaced as
    /// Completed (with cancelled:true), mirroring the auth flow: the status-aware frontend handler only
    /// treats completed/failed as terminal, so a raw Cancelled status would leave the card stuck.
    /// </summary>
    private Task EmitRefreshTerminalAsync(OperationTerminalInfo info, Guid operationId)
    {
        var status = info.Success || info.Cancelled ? OperationStatus.Completed : OperationStatus.Failed;
        var percent = info.Success || info.Cancelled ? 100.0 : _refreshHighestPercent;
        var stageKey = info.Success
            ? "signalr.xboxMapping.gamesDiscovered"
            : info.Cancelled ? "signalr.xboxMapping.cancelled" : "signalr.xboxMapping.failed";
        var context = info.Success
            ? new Dictionary<string, object?> { ["gamesDiscovered"] = _gamesDiscovered }
            : info.Cancelled
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["errorDetail"] = info.Error };

        return _notifications.NotifyAllAsync(SignalREvents.XboxMappingProgress, new
        {
            operationId,
            success = info.Success,
            status,
            stageKey,
            percentComplete = percent,
            gamesDiscovered = _gamesDiscovered,
            cancelled = info.Cancelled,
            error = info.Success || info.Cancelled ? null : info.Error,
            context,
            message = string.Empty,
            isTerminal = true,
            showNotification = _refreshShowNotification
        });
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
                await RefreshNowAsync(token);
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
