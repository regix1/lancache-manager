using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Consolidated per-minute reaper for ALL 5 prefill daemon platforms' session expiry, replacing the
/// 5 independent, uncoordinated <see cref="System.Threading.Timer"/> instances that used to live one
/// per <see cref="PrefillDaemonServiceBase"/> subclass. Each tick resolves every platform's daemon
/// singleton and calls its own <see cref="PrefillDaemonServiceBase.ProcessSessionExpiryAsync"/>, which
/// owns the actual per-session decision (flag / terminate / stall-fail) via
/// <see cref="LancacheManager.Infrastructure.Services.ScheduledPrefill.PrefillSessionExpiryGates"/>.
/// Hardcoded to the same 1-minute cadence the old Timers used - this is infrastructure polling, not a
/// user-facing schedule (matches <see cref="ScheduledPrefillService"/>'s rationale for the same
/// choice). Deliberately NOT added to <c>ServiceScheduleRegistry</c>'s allowlist, since the mechanism
/// it replaces was never Schedules-page-visible either; <see cref="ServiceKey"/> is still set to a
/// real value so opting into Schedules-page visibility later is a one-line allowlist addition rather
/// than also needing a new key. Platforms are resolved and processed concurrently (see
/// <see cref="ProcessPlatformExpiryAsync"/>) so the 5 independent Timers' effective concurrency is
/// preserved rather than collapsed into one serial chain.
/// </summary>
public sealed class PersistentSessionExpiryService : ScheduledBackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public PersistentSessionExpiryService(
        ILogger<PersistentSessionExpiryService> logger,
        IConfiguration configuration,
        IServiceScopeFactory serviceScopeFactory)
        : base(logger, configuration)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    protected override string ServiceName => "PersistentSessionExpiry";

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    public override string ServiceKey => "persistentSessionExpiry";

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var provider = scope.ServiceProvider;
        var nowUtc = DateTime.UtcNow;

        // Enumerate the platform enum directly (no DI calls here) rather than going through
        // ResolveAllDaemons, so resolution AND processing for each platform share the SAME
        // try/catch inside ProcessPlatformExpiryAsync below - a DI failure for one platform must
        // not prevent the others' tasks from running. Each platform's work is its own Task so a
        // slow teardown on one platform (Docker stop/remove + DB writes) no longer delays the
        // others in the same tick.
        var platformTasks = Enum.GetValues<PrefillPlatform>()
            .Select(platform => ProcessPlatformExpiryAsync(provider, platform, nowUtc, stoppingToken))
            .ToList();

        await Task.WhenAll(platformTasks);
    }

    private async Task ProcessPlatformExpiryAsync(
        IServiceProvider provider,
        PrefillPlatform platform,
        DateTime nowUtc,
        CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        try
        {
            // Resolution happens INSIDE this try/catch. GetRequiredService can throw for a
            // misconfigured platform, and this keeps that failure isolated to this platform's
            // task instead of escaping the whole foreach/Task.WhenAll the way it would if
            // resolution happened lazily outside of this try (as it did via the old
            // ResolveAllDaemons-iterator foreach, where GetRequiredService ran inside MoveNext()).
            var daemon = PrefillDaemonServiceBase.ResolveDaemon(provider, platform);
            if (daemon == null)
            {
                return;
            }

            var result = await daemon.ProcessSessionExpiryAsync(nowUtc);

            if (result.FlaggedNeedsRelogin > 0 || result.Terminated > 0 || result.StalledFailed > 0)
            {
                _logger.LogInformation(
                    "[PersistentSessionExpiry] {Daemon}: flaggedNeedsRelogin={FlaggedNeedsRelogin}, terminated={Terminated}, stalledFailed={StalledFailed}",
                    daemon.GetType().Name, result.FlaggedNeedsRelogin, result.Terminated, result.StalledFailed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PersistentSessionExpiry] Error processing session expiry for platform {Platform}",
                platform);
        }
    }
}
