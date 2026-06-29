using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Orchestrates scheduled prefill runs across all enabled services on a user-configurable
/// interval (managed through the unified Schedules page via <see cref="ServiceScheduleRegistry"/>).
/// Lane B1 provided the compiling skeleton; Lane B2 fills in the per-service lifecycle.
/// </summary>
public sealed class ScheduledPrefillService : ConfigurableScheduledService
{
    private static readonly TimeSpan _defaultInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;

    // Retained (injected but no longer called) now that scheduled prefill reuses the already
    // logged-in persistent container instead of headlessly authenticating a fresh guest container.
    // Removing the service + its DI registration is a separate, verified follow-up.
    private readonly IScheduledPrefillAuthService _authService;
    private readonly IStateService _stateService;

    /// <summary>
    /// Deterministic pseudo-user Guid that owns every daemon session created by the scheduler.
    /// Derived once from <see cref="ScheduledPrefillConstants.SystemUserId"/> so that the busy
    /// check can reliably distinguish "our" system sessions from real manual-user sessions.
    /// </summary>
    private readonly Guid _systemUserId = ScheduledPrefillConstants.DeriveSystemUserId();

    /// <summary>
    /// Stable service key used by <see cref="ServiceScheduleRegistry"/> (read via reflection)
    /// and by <c>StateService.ServiceIntervals</c>.
    /// </summary>
    public string ScheduleServiceKey => "scheduledPrefill";

    /// <summary>
    /// Log-friendly name surfaced by the base class in log lines.
    /// </summary>
    protected override string ServiceName => "Scheduled Prefill";

    /// <summary>
    /// Scheduled prefill should not fire automatically the instant the app starts.
    /// </summary>
    public override bool DefaultRunOnStartup => false;

    public ScheduledPrefillService(
        ILogger<ScheduledPrefillService> logger,
        IServiceScopeFactory scopeFactory,
        IScheduledPrefillAuthService authService,
        IStateService stateService)
        : base(logger, _defaultInterval)
    {
        _scopeFactory = scopeFactory;
        _authService = authService;
        _stateService = stateService;

        // Apply any user-saved interval / run-on-startup overrides from state.json before
        // the scheduling loop starts. Matches GcScheduledService / SteamKit2Service pattern.
        LoadStateOverrides(stateService, ScheduleServiceKey);
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var config = _stateService.GetScheduledPrefillConfig();
        var services = config.GetEnabledServicesInRunOrder();

        if (services.Count == 0)
        {
            _logger.LogInformation("[ScheduledPrefill] No services enabled; nothing to do");
            using var emptyScope = _scopeFactory.CreateScope();
            var emptyNotifications = emptyScope.ServiceProvider.GetRequiredService<ISignalRNotificationService>();
            await emptyNotifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
            {
                operationId = (string?)null,
                success = false,
                error = "No services enabled"
            });
            return;
        }

        _logger.LogInformation("[ScheduledPrefill] Starting run for {Count} service(s)", services.Count);

        // Register a single tracker operation for the whole run. The tracker owns the CTS after a
        // successful RegisterOperation, so we link it to stoppingToken and never dispose it here.
        using var scope = _scopeFactory.CreateScope();
        var tracker = scope.ServiceProvider.GetRequiredService<IUnifiedOperationTracker>();
        var notifications = scope.ServiceProvider.GetRequiredService<ISignalRNotificationService>();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var operationId = tracker.RegisterOperation(
            OperationType.ScheduledPrefill,
            "Scheduled Prefill",
            cts);
        var operationIdString = operationId.ToString();
        var runToken = cts.Token;

        bool success = true;
        string? error = null;

        try
        {
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillStarted, new
            {
                operationId = operationIdString,
                serviceCount = services.Count
            });

            var servicesAttempted = 0;

            foreach (var serviceConfig in services)
            {
                runToken.ThrowIfCancellationRequested();

                try
                {
                    if (await RunServiceAsync(serviceConfig, operationIdString, scope.ServiceProvider, notifications, config, runToken))
                    {
                        servicesAttempted++;
                    }
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One service failing must not abort the rest of the run.
                    _logger.LogError(ex, "[ScheduledPrefill] Service {Service} failed; continuing", serviceConfig.ServiceId);
                }
            }

            if (servicesAttempted == 0)
            {
                success = false;
                error = "All enabled services were skipped";
            }
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            success = false;
            error = "Scheduled prefill run cancelled";
            throw;
        }
        catch (Exception ex)
        {
            success = false;
            error = ex.Message;
            throw;
        }
        finally
        {
            await notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillCompleted, new
            {
                operationId = operationIdString,
                success,
                error
            });

            // Tracker disposes the adopted CTS exactly once inside CompleteOperation; we must not.
            tracker.CompleteOperation(operationId, success, error);
        }

        _logger.LogInformation("[ScheduledPrefill] Run complete");
    }

    /// <summary>
    /// Runs a single service's scheduled prefill: resolves the concrete daemon, reuses the running
    /// persistent admin container (which authenticates itself from its named auth volume), performs
    /// the needs-login + busy gates, and drives the prefill on that persistent session. The
    /// persistent container is system-owned and long-lived, so it is never created or torn down
    /// here. Emits <see cref="SignalREvents.ScheduledPrefillProgress"/> at each stage.
    /// </summary>
    /// <returns>True when the service engaged the persistent container's prefill; false when skipped.</returns>
    private async Task<bool> RunServiceAsync(
        ScheduledPrefillServiceConfigDto serviceConfig,
        string operationId,
        IServiceProvider serviceProvider,
        ISignalRNotificationService notifications,
        ScheduledPrefillConfigDto config,
        CancellationToken ct)
    {
        var serviceId = serviceConfig.ServiceId;

        // 1. Resolve the concrete daemon service for this platform.
        var daemon = ResolveDaemon(serviceProvider, serviceId);
        if (daemon is null)
        {
            await EmitProgressAsync(notifications, operationId, serviceId, "skipped", "No daemon registered for this service");
            return false;
        }

        // 2. Reuse the running persistent admin container. Scheduled prefill is admin-only and
        // downloads INSIDE the long-lived persistent container, which authenticates itself from its
        // named auth volume. It never spawns a temporary guest container and never injects a token.
        var persistentSession = daemon.GetActivePersistentSession();
        if (!ScheduledPrefillRunGates.TryGetRunnablePersistentSession(persistentSession, out var sessionId, out var needsLoginReason))
        {
            await EmitProgressAsync(
                notifications,
                operationId,
                serviceId,
                "needs-login",
                $"No logged-in persistent container for {serviceId}",
                needsLoginReason);
            return false;
        }

        // TryGetRunnablePersistentSession only returns true for a non-null, authenticated session.
        var session = persistentSession!;

        // 3. Busy check: defer when the persistent container is already prefilling (a prior run still
        // going) or a manual/guest interactive session is live. An idle persistent container does not
        // block, because it shares the system user id with the scheduler.
        if (ScheduledPrefillRunGates.ShouldSkipForBusySessions(
                daemon.GetAllSessions(),
                _systemUserId,
                out var skipMessage))
        {
            await EmitProgressAsync(notifications, operationId, serviceId, "skipped", skipMessage);
            return false;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceId,
            "starting",
            "Reusing persistent container",
            downloadSessionId: sessionId);

        // 4. Kick off the prefill. Map preset + OS list to the real daemon signature.
        // When specific apps are selected, prefill exactly those and ignore the All/Recent/Top
        // preset; otherwise fall back to the preset selection.
        var hasSelectedApps = serviceConfig.SelectedAppIds.Count > 0;
        bool all;
        bool recent;
        int? top;
        if (hasSelectedApps)
        {
            all = false;
            recent = false;
            top = null;
        }
        else
        {
            MapPreset(serviceConfig, out all, out recent, out top);
        }

        var operatingSystems = MapOperatingSystems(serviceConfig.OperatingSystems);
        var maxConcurrency = serviceConfig.MaxConcurrency.Mode == ScheduledPrefillMaxConcurrencyMode.Fixed
            ? serviceConfig.MaxConcurrency.Value
            : null;

        if (hasSelectedApps)
        {
            _logger.LogInformation(
                "[ScheduledPrefill] Setting {Count} selected app(s) on session {SessionId}",
                serviceConfig.SelectedAppIds.Count,
                sessionId);
            await daemon.SetSelectedAppsAsync(sessionId, serviceConfig.SelectedAppIds, ct);
        }

        _logger.LogInformation(
            "[ScheduledPrefill] Starting prefill on persistent session {SessionId} (force={Force}, selectedApps={SelectedCount})",
            sessionId,
            serviceConfig.Force,
            serviceConfig.SelectedAppIds.Count);

        PrefillResult result;
        try
        {
            result = await daemon.PrefillAsync(
                sessionId,
                all: all,
                recent: recent,
                recentlyPurchased: false,
                top: top,
                force: serviceConfig.Force,
                operatingSystems: operatingSystems,
                maxConcurrency: maxConcurrency,
                cancellationToken: ct);
        }
        catch (PrefillAlreadyRunningException)
        {
            await EmitProgressAsync(notifications, operationId, serviceId, "skipped", "A prefill is already in progress");
            return true;
        }

        // A failed start may leave IsPrefilling already false, which would make the poll loop
        // exit immediately and wrongly report "completed". Treat a non-Success start as failed.
        if (!result.Success)
        {
            var failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "Prefill failed to start"
                : result.ErrorMessage;
            await EmitProgressAsync(notifications, operationId, serviceId, "failed", failureMessage);
            return true;
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceId,
            "running",
            "Prefill in progress",
            downloadSessionId: sessionId);

        // Poll until the daemon clears IsPrefilling, or a guard trips. The persistent container is
        // never terminated here; on cancellation we only cancel the in-flight prefill.
        var runDeadline = DateTime.UtcNow + config.MaxServiceRuntime;
        while (session.IsPrefilling)
        {
            ct.ThrowIfCancellationRequested();

            if (DateTime.UtcNow >= runDeadline)
            {
                await EmitProgressAsync(notifications, operationId, serviceId, "failed", "Exceeded maximum service runtime");
                return true;
            }

            if (PrefillDaemonServiceBase.IsPrefillStalled(session, DateTime.UtcNow, config.StallTimeout))
            {
                await EmitProgressAsync(notifications, operationId, serviceId, "failed", "Prefill stalled (no progress)");
                return true;
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                await daemon.CancelPrefillAsync(sessionId, CancellationToken.None);
                throw;
            }
        }

        await EmitProgressAsync(
            notifications,
            operationId,
            serviceId,
            "completed",
            BuildCompletionMessage(session, hasSelectedApps, serviceConfig.Force),
            bytesDownloaded: session.TotalBytesTransferred,
            downloadSessionId: sessionId);
        return true;
    }

    /// <summary>
    /// Resolves the concrete daemon singleton for a platform. Each daemon is registered as its
    /// own concrete type (see Program.cs <c>AddSingletonHostedService&lt;T&gt;</c>), so we resolve
    /// it directly. Returns null for any platform without a daemon registration.
    /// </summary>
    private static PrefillDaemonServiceBase? ResolveDaemon(IServiceProvider provider, PrefillPlatform platform)
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

    private static void MapPreset(ScheduledPrefillServiceConfigDto serviceConfig, out bool all, out bool recent, out int? top)
    {
        all = false;
        recent = false;
        top = null;

        switch (serviceConfig.Preset)
        {
            case ScheduledPrefillPreset.All:
                all = true;
                break;
            case ScheduledPrefillPreset.Recent:
                recent = true;
                break;
            case ScheduledPrefillPreset.Top:
                top = serviceConfig.TopCount;
                break;
        }
    }

    private static List<string>? MapOperatingSystems(List<ScheduledPrefillOperatingSystem> operatingSystems)
    {
        if (operatingSystems.Count == 0)
        {
            return null;
        }

        var mapped = new List<string>(operatingSystems.Count);
        foreach (var os in operatingSystems)
        {
            mapped.Add(os switch
            {
                ScheduledPrefillOperatingSystem.Windows => "windows",
                ScheduledPrefillOperatingSystem.Linux => "linux",
                ScheduledPrefillOperatingSystem.Macos => "macos",
                _ => os.ToString().ToLowerInvariant()
            });
        }

        return mapped;
    }

    private static string BuildCompletionMessage(DaemonSession session, bool hasSelectedApps, bool force)
    {
        var bytes = session.TotalBytesTransferred;
        if (bytes > 0)
        {
            return $"Prefill completed ({FormattingUtils.FormatBytes(bytes)} downloaded)";
        }

        if (hasSelectedApps && !force)
        {
            return "Prefill completed — all selected games were already cached (0 bytes). Enable Force to re-download.";
        }

        return "Prefill completed (0 bytes downloaded)";
    }

    private Task EmitProgressAsync(
        ISignalRNotificationService notifications,
        string operationId,
        PrefillPlatform serviceId,
        string stage,
        string message,
        string? needsLoginReason = null,
        long? bytesDownloaded = null,
        string? downloadSessionId = null)
    {
        _logger.LogInformation("[ScheduledPrefill] {Service} {Stage}: {Message}", serviceId, stage, message);
        return notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillProgress, new
        {
            operationId,
            serviceId = serviceId.ToString(),
            stage,
            message,
            needsLoginReason,
            bytesDownloaded,
            downloadSessionId
        });
    }
}
