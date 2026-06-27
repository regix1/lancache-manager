using System.Security.Cryptography;
using System.Text;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly IScheduledPrefillAuthService _authService;
    private readonly IStateService _stateService;

    /// <summary>
    /// Deterministic pseudo-user Guid that owns every daemon session created by the scheduler.
    /// Derived once from <see cref="ScheduledPrefillConstants.SystemUserId"/> so that the busy
    /// check can reliably distinguish "our" system sessions from real manual-user sessions.
    /// </summary>
    private readonly Guid _systemUserId = DeriveSystemUserId();

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

            foreach (var serviceConfig in services)
            {
                runToken.ThrowIfCancellationRequested();

                try
                {
                    await RunServiceAsync(serviceConfig, operationIdString, scope.ServiceProvider, notifications, config, runToken);
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
                success
            });

            // Tracker disposes the adopted CTS exactly once inside CompleteOperation; we must not.
            tracker.CompleteOperation(operationId, success, error);
        }

        _logger.LogInformation("[ScheduledPrefill] Run complete");
    }

    /// <summary>
    /// Runs a single service's scheduled prefill: resolves the concrete daemon, performs the
    /// busy/auth gates, creates a system-owned session, drives the prefill, and guarantees
    /// teardown. Emits <see cref="SignalREvents.ScheduledPrefillProgress"/> at each stage.
    /// </summary>
    private async Task RunServiceAsync(
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
            return;
        }

        // 2. Busy check: never disturb a live manual session.
        var activeManualSession = daemon.GetAllSessions().Any(s =>
            s.Status == DaemonSessionStatus.Active && s.UserId != _systemUserId);
        if (activeManualSession || daemon.IsAnyDaemonAuthenticated())
        {
            await EmitProgressAsync(notifications, operationId, serviceId, "skipped", "A manual prefill session is active");
            return;
        }

        // 3. Resolve the auth plan for this service.
        var plan = await _authService.EnsureAuthenticatedAsync(
            serviceId,
            new ScheduledPrefillAuthContext
            {
                Service = serviceId,
                UserId = ScheduledPrefillConstants.SystemUserId
            },
            ct);

        if (plan.State == ScheduledPrefillAuthState.NeedsLogin)
        {
            await EmitProgressAsync(
                notifications,
                operationId,
                serviceId,
                "needs-login",
                "Service requires a login before it can be scheduled",
                plan.NeedsLoginReason);
            return;
        }

        // 4. Create the system-owned daemon session.
        await EmitProgressAsync(notifications, operationId, serviceId, "starting", "Creating daemon session");
        var session = await daemon.CreateSessionAsync(_systemUserId, cancellationToken: ct);
        var sessionId = session.Id;
        bool weCreatedSession = true;

        try
        {
            if (plan.AfterSessionCreatedAsync is not null)
            {
                await plan.AfterSessionCreatedAsync(session, ct);
            }

            // 5. Kick off the prefill. Map preset + OS list to the real daemon signature.
            MapPreset(serviceConfig, out bool all, out bool recent, out int? top);
            var operatingSystems = MapOperatingSystems(serviceConfig.OperatingSystems);
            var maxConcurrency = serviceConfig.MaxConcurrency.Mode == ScheduledPrefillMaxConcurrencyMode.Fixed
                ? serviceConfig.MaxConcurrency.Value
                : null;

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
                return;
            }

            // A failed start may leave IsPrefilling already false, which would make the poll loop
            // exit immediately and wrongly report "completed". Treat a non-Success start as failed.
            if (!result.Success)
            {
                var failureMessage = string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "Prefill failed to start"
                    : result.ErrorMessage;
                await EmitProgressAsync(notifications, operationId, serviceId, "failed", failureMessage);
                return;
            }

            await EmitProgressAsync(notifications, operationId, serviceId, "running", "Prefill in progress");

            // Poll until the daemon clears IsPrefilling, or a guard trips.
            var runDeadline = DateTime.UtcNow + config.MaxServiceRuntime;
            while (session.IsPrefilling)
            {
                ct.ThrowIfCancellationRequested();

                if (DateTime.UtcNow >= runDeadline)
                {
                    await EmitProgressAsync(notifications, operationId, serviceId, "failed", "Exceeded maximum service runtime");
                    return;
                }

                if (PrefillDaemonServiceBase.IsPrefillStalled(session, DateTime.UtcNow, config.StallTimeout))
                {
                    await EmitProgressAsync(notifications, operationId, serviceId, "failed", "Prefill stalled (no progress)");
                    return;
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

            await EmitProgressAsync(notifications, operationId, serviceId, "completed", "Prefill completed");
        }
        finally
        {
            // Guaranteed teardown: we own the session, so always terminate it.
            if (weCreatedSession)
            {
                try
                {
                    await daemon.TerminateSessionAsync(sessionId, reason: "Scheduled prefill complete", force: true, terminatedBy: "system");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ScheduledPrefill] Failed to terminate session {SessionId} for {Service}", sessionId, serviceId);
                }
            }

            if (plan.CleanupAsync is not null)
            {
                try
                {
                    await plan.CleanupAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ScheduledPrefill] Auth cleanup failed for {Service}", serviceId);
                }
            }
        }
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

    private Task EmitProgressAsync(
        ISignalRNotificationService notifications,
        string operationId,
        PrefillPlatform serviceId,
        string stage,
        string message,
        string? needsLoginReason = null)
    {
        _logger.LogInformation("[ScheduledPrefill] {Service} {Stage}: {Message}", serviceId, stage, message);
        return notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillProgress, new
        {
            operationId,
            serviceId = serviceId.ToString(),
            stage,
            message,
            needsLoginReason
        });
    }

    /// <summary>
    /// Derives a stable Guid from <see cref="ScheduledPrefillConstants.SystemUserId"/> so every
    /// scheduler-owned session shares one identity, distinguishable from real users.
    /// </summary>
    private static Guid DeriveSystemUserId()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ScheduledPrefillConstants.SystemUserId));
        var bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }
}
