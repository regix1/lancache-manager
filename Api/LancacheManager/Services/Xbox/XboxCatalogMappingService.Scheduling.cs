using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// The Xbox catalog-mapping run itself. Each mapping service keeps its own pipeline because the work
/// genuinely differs - Xbox collects from two catalog sources and then resolves and backfills, Epic
/// resolves only, Steam crawls PICS - so only the reporting contract is shared
/// (<see cref="MappingOperationReporter"/>). Everything else about the service, including the DI
/// dependencies and the daemon subscriptions this file reads, lives in XboxCatalogMappingService.cs.
/// </summary>
public partial class XboxCatalogMappingService
{
    /// <summary>Terminal stage key for a run that collected nothing because Xbox is signed out.</summary>
    private const string XboxSignInSkipStageKey = "signalr.xboxMapping.skippedNotSignedIn";

    /// <summary>
    /// Scheduled tick: collect the catalog from authenticated daemon sessions and resolve downloads.
    /// </summary>
    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await RefreshNowAsync(stoppingToken, CurrentRunTrigger);
    }

    /// <summary>
    /// Runs one catalog collection + download-resolution pass. Shared by the scheduled tick and the
    /// on-authentication nudge. Serialized via <c>_refreshGate</c>. When neither the manager-side MSA
    /// session nor any prefill daemon session is signed in there is no catalog to read, so the run
    /// reports itself skipped instead of walking the pipeline and claiming a completed refresh.
    /// </summary>
    /// <param name="trigger">Why this pass is running. The caller supplies it because a nudge from a
    /// fresh daemon login is a user action, while the loop's own tick is not, and the notification
    /// mode the user set gates the two differently.</param>
    public async Task<XboxCatalogRefreshResult> RefreshNowAsync(
        CancellationToken ct = default,
        RunTrigger trigger = RunTrigger.Manual)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            _refreshShowNotification = EffectiveNotificationMode.AllowsTrigger(trigger);
            await using var reporter = new MappingOperationReporter(
                _notifications,
                _operationTracker,
                MappingOperations.Xbox,
                _refreshShowNotification,
                ct,
                _logger);
            _currentMappingReporter = reporter;

            try
            {
                await reporter.StartAsync(CreateXboxMappingContext());

                var daemon = ResolveDaemonService();
                if (daemon == null)
                {
                    _logger.LogDebug("XboxPrefillDaemonService unavailable - skipping the daemon catalog source");
                }

                // Being signed in decides only whether there is a NEW catalog to collect. The two
                // collection steps below are already gated individually; the two after them need no
                // account at all - resolving downloads matches against patterns already in the
                // database, and banner backfill reads the public DisplayCatalog with no token.
                // Returning here would leave every download logged since the last signed-in pass
                // sitting as an unnamed row, which for a daemon-only setup is nearly always.
                var signedIn = IsAuthenticated || daemon?.IsAnyDaemonAuthenticated() == true;
                if (!signedIn)
                {
                    _logger.LogInformation(
                        "Xbox catalog refresh: no Microsoft account is signed in, so no new catalog is collected - resolving downloads against the stored patterns instead");

                    // No catalog collection to show, and this pass may well change nothing, so it goes
                    // quiet rather than claiming 20/45/70/90 and then reporting it did nothing.
                    reporter.SuppressProgress();
                }

                var newPatterns = 0;
                await reporter.ReportAsync(
                    20,
                    "signalr.xboxMapping.collecting",
                    CreateXboxMappingContext());
                if (IsAuthenticated)
                {
                    newPatterns += await HarvestManagerCatalogAsync(reporter.Token);
                }

                await reporter.ReportAsync(
                    45,
                    "signalr.xboxMapping.collecting",
                    CreateXboxMappingContext(newPatterns: newPatterns));
                if (daemon != null)
                {
                    newPatterns += await daemon.RefreshCatalogFromActiveSessionsAsync(reporter.Token);
                }

                await reporter.ReportAsync(
                    70,
                    "signalr.xboxMapping.resolving",
                    CreateXboxMappingContext(newPatterns: newPatterns));
                var resolved = await _mappingService.ResolveDownloadsAsync(reporter.Token);

                await reporter.ReportAsync(
                    90,
                    "signalr.xboxMapping.backfilling",
                    CreateXboxMappingContext(newPatterns, resolved));
                var bannersStored = 0;
                try
                {
                    bannersStored = await _mappingService.BackfillMissingBannerArtAsync(reporter.Token);
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
                    "Xbox catalog refresh: {NewPatterns} new CDN pattern(s), {Resolved} download(s) re-tagged, {Banners} banner(s) stored",
                    newPatterns, resolved, bannersStored);

                // Report skipped only when nobody was signed in AND the pass changed nothing, so a
                // signed-out run that still did work reads as the work it was. Banner backfill counts
                // here because it writes to the database - it needs no account, so a signed-out pass
                // can store art, and calling that run "skipped" would claim it did nothing.
                var changedNothing = !signedIn && newPatterns == 0 && resolved == 0 && bannersStored == 0;
                if (changedNothing)
                {
                    await reporter.CompleteSkippedAsync(
                        XboxSignInSkipStageKey,
                        CreateXboxMappingContext(newPatterns, resolved));
                }
                else
                {
                    await reporter.CompleteAsync(
                        success: true,
                        context: CreateXboxMappingContext(newPatterns, resolved));
                }

                return new XboxCatalogRefreshResult { NewPatterns = newPatterns, Resolved = resolved };
            }
            catch (OperationCanceledException)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: "Cancelled by user",
                    cancelled: true,
                    context: CreateXboxMappingContext());
                throw;
            }
            catch (Exception ex)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: ex.Message,
                    context: CreateXboxMappingContext(errorDetail: ex.Message));
                throw;
            }
            finally
            {
                if (ReferenceEquals(_currentMappingReporter, reporter))
                {
                    _currentMappingReporter = null;
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private Dictionary<string, object?> CreateXboxMappingContext(
        int newPatterns = 0,
        int resolved = 0,
        string? errorDetail = null) =>
        new()
        {
            ["gamesDiscovered"] = _gamesDiscovered,
            ["newPatterns"] = newPatterns,
            ["resolved"] = resolved,
            ["errorDetail"] = errorDetail,
        };
}
