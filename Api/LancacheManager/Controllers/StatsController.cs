using LancacheManager.Models;
using LancacheManager.Configuration;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Extensions;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Net;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for statistics and analytics
/// Handles client stats, service stats, and dashboard metrics
/// </summary>
[ApiController]
[Route("api/stats")]
[Authorize]
public class StatsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IClientGroupsService _clientGroupsRepository;
    private readonly CacheSnapshotService _cacheSnapshotService;
    private readonly IStateService _stateRepository;
    private readonly IOptions<ApiOptions> _apiOptions;
    private readonly ISignalRNotificationService _notifications;
    private readonly CacheReconciliationService _reconciliationService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IOperationQueue _operationQueue;
    private readonly IServiceScheduleRegistry _scheduleRegistry;
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly IClientHostnameService _clientHostnameService;
    private readonly IEventsService _eventsService;

    public StatsController(
        AppDbContext context,
        IClientGroupsService clientGroupsRepository,
        CacheSnapshotService cacheSnapshotService,
        IStateService stateRepository,
        IOptions<ApiOptions> apiOptions,
        ISignalRNotificationService notifications,
        CacheReconciliationService reconciliationService,
        IUnifiedOperationTracker operationTracker,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue,
        IServiceScheduleRegistry scheduleRegistry,
        DatasourceCapabilityService capabilityService,
        IClientHostnameService clientHostnameService,
        IEventsService eventsService)
    {
        _clientHostnameService = clientHostnameService;
        _capabilityService = capabilityService;
        _context = context;
        _clientGroupsRepository = clientGroupsRepository;
        _cacheSnapshotService = cacheSnapshotService;
        _stateRepository = stateRepository;
        _apiOptions = apiOptions;
        _notifications = notifications;
        _reconciliationService = reconciliationService;
        _operationTracker = operationTracker;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
        _scheduleRegistry = scheduleRegistry;
        _eventsService = eventsService;
    }

    /// <summary>
    /// Converts a single event ID into a list for filtering.
    /// </summary>
    private static List<long> ParseEventId(long? eventId)
    {
        if (!eventId.HasValue)
            return new List<long>();

        return new List<long> { eventId.Value };
    }

    /// <summary>
    /// Gets download IDs tagged to specific events.
    /// Used to filter stats to only show downloads associated with events.
    /// </summary>
    private async Task<HashSet<long>> GetEventDownloadIdsAsync(List<long> eventIds)
    {
        if (eventIds.Count == 0)
            return new HashSet<long>();

        var downloadIds = await _context.EventDownloads
            .AsNoTracking()
            .Where(ed => eventIds.Contains(ed.EventId))
            .Select(ed => ed.DownloadId)
            .Distinct()
            .ToListAsync();
        return downloadIds.ToHashSet();
    }

    /// <summary>
    /// Builds the common base downloads query: hidden client filter + evicted filter.
    /// Avoids repeating this filter chain across multiple endpoints.
    /// </summary>
    private IQueryable<Download> BaseDownloadsQuery(List<string> hiddenClientIps, string evictedMode)
    {
        return _context.Downloads.AsNoTracking()
            .ApplyHiddenClientFilter(hiddenClientIps)
            .ApplyEvictedFilter(evictedMode);
    }

    /// <summary>
    /// Generic aggregate method that applies a selector to a downloads query,
    /// optionally excluding stats-excluded IPs.
    /// Uses a single atomic query to prevent race conditions from concurrent data changes.
    /// </summary>
    private static async Task<T> AggregateExcludingAsync<T>(
        IQueryable<Download> query,
        List<string> statsExcludedIps,
        Func<IQueryable<Download>, Task<T>> aggregator)
    {
        if (statsExcludedIps.Count == 0)
        {
            return await aggregator(query);
        }

        // Single atomic query - filter out excluded IPs directly
        // This prevents race conditions where data changes between queries
        var filtered = query.Where(d => !statsExcludedIps.Contains(d.ClientIp));
        return await aggregator(filtered);
    }

    private static List<string> NormalizeClientIps(IEnumerable<string>? ips, out List<string> invalidIps)
    {
        invalidIps = new List<string>();
        var normalized = new List<string>();

        if (ips == null)
        {
            return normalized;
        }

        foreach (var rawIp in ips)
        {
            var trimmed = rawIp?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var parsed))
            {
                invalidIps.Add(trimmed);
                continue;
            }

            var normalizedIp = parsed.ToString();
            if (!normalized.Contains(normalizedIp))
            {
                normalized.Add(normalizedIp);
            }
        }

        return normalized;
    }

    private static List<ClientExclusionRule> NormalizeClientRules(
        IEnumerable<ClientExclusionRule>? rules,
        out List<string> invalidIps)
    {
        invalidIps = new List<string>();
        var normalized = new List<ClientExclusionRule>();

        if (rules == null)
        {
            return normalized;
        }

        foreach (var rule in rules)
        {
            var trimmed = rule?.Ip?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (!IPAddress.TryParse(trimmed, out var parsed))
            {
                invalidIps.Add(trimmed);
                continue;
            }

            var normalizedIp = parsed.ToString();
            if (normalized.Any(r => r.Ip == normalizedIp))
            {
                continue;
            }

            // Anything other than an explicit "exclude" collapses to "hide" (matches StateService).
            var mode = string.Equals(rule?.Mode, ClientExclusionModes.Exclude, StringComparison.OrdinalIgnoreCase)
                ? ClientExclusionModes.Exclude
                : ClientExclusionModes.Hide;

            normalized.Add(new ClientExclusionRule { Ip = normalizedIp, Mode = mode });
        }

        return normalized;
    }


    /// <summary>
    /// Gets per-client traffic totals ranked by total bytes.
    /// </summary>
    /// <remarks>
    /// Client-group members are folded together into one row, with an optional event filter.
    /// Hidden and stats-excluded clients are dropped unless <paramref name="includeExcluded"/> is set.
    /// </remarks>
    /// <param name="includeExcluded">
    /// Skips the hidden/stats-excluded client filters when true. Client management passes this so
    /// admins can still pick any known client even while it is excluded from normal stats.
    /// </param>
    [HttpGet("clients")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(List<ClientStatsWithGroup>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ClientStatsWithGroup>>> GetClientsAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] int? limit = null,
        [FromQuery] long? eventId = null,
        [FromQuery] bool includeExcluded = false,
        CancellationToken ct = default)
    {
        // Get configuration
        var maxLimit = _apiOptions.Value.MaxClientsPerRequest;
        var defaultLimit = _apiOptions.Value.DefaultClientsLimit;
        var effectiveLimit = Math.Min(limit ?? defaultLimit, maxLimit);

        // Parse event IDs
        var eventIdList = ParseEventId(eventId);

        // A cascade delete removes the event's EventDownloads rows, so an unknown id would
        // otherwise flow through ApplyEventFilter as an empty (but 200 OK) result instead of
        // a clear signal that the id is gone.
        if (eventId.HasValue)
        {
            await _eventsService.GetByIdOrThrowAsync(eventId.Value, "Event", ct);
        }

        // Build base query with time filtering
        var query = _context.Downloads.AsNoTracking();

        // Apply event filter if provided (filters to only tagged downloads)
        HashSet<long>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);

        // Normal stats omit hidden clients and stats-only exclusions.
        // Management passes includeExcluded=true so admins can still pick any known client.
        var hiddenClientIps = includeExcluded ? new List<string>() : _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = includeExcluded ? new List<string>() : _stateRepository.GetStatsExcludedOnlyClientIps();
        query = query.ApplyHiddenClientFilter(hiddenClientIps);
        if (statsExcludedOnlyIps.Count > 0)
            query = query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp));

        var evictedMode = _stateRepository.GetEvictedDataMode();
        query = query.ApplyEvictedFilter(evictedMode);

        if (startTime.HasValue)
        {
            var startDate = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= startDate);
        }
        if (endTime.HasValue)
        {
            var endDate = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDate);
        }

        // The GROUP BY and its client-side duration fold are shared with the dashboard batch, so
        // the two surfaces cannot drift at the step above the shared ranking helper.
        var ipAggregates = await ClientStatsAggregationHelper.QueryIpAggregatesAsync(query, ct);

        var ipToGroupMapping = await _clientGroupsRepository.GetIpMappingAsync(ct);

        // Empty unless the hostname lookup is on, in which case a row with no nickname is labelled
        // with the machine's own name instead of its address. Only the addresses that will be
        // displayed are resolved, so the busiest clients are the ones that get names.
        var ipToHostname = (await _clientHostnameService.ResolveAsync(
            ClientStatsAggregationHelper.TopClientIpsByTraffic(ipAggregates, effectiveLimit), ct)).Hostnames;

        // Group members are folded before the limit is applied, so a nickname spread over
        // several IPs ranks on its combined traffic. IMPROVEMENT #4: configurable limit.
        var allStats = ClientStatsAggregationHelper.AggregateAndRank(
            ipAggregates, ipToGroupMapping, ipToHostname, effectiveLimit);

        return Ok(allStats);
    }

    /// <summary>
    /// Gets the current stats exclusion settings.
    /// </summary>
    /// <remarks>
    /// Returns the legacy stats-excluded-only IP list plus the full mode-aware rule set (hide vs exclude).
    /// </remarks>
    [HttpGet("exclusions")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(StatsExclusionsResponse), StatusCodes.Status200OK)]
    public ActionResult<StatsExclusionsResponse> GetExcludedClients()
    {
        return Ok(new StatsExclusionsResponse
        {
            Ips = _stateRepository.GetStatsExcludedOnlyClientIps(),
            Rules = _stateRepository.GetExcludedClientRules()
        });
    }

    /// <summary>
    /// Replaces the stats exclusion rules.
    /// </summary>
    /// <remarks>
    /// Accepts the mode-aware rule set when provided, falling back to the legacy IP-only list
    /// otherwise, so older clients keep working.
    /// </remarks>
    [HttpPut("exclusions")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(StatsExclusionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<StatsExclusionsResponse>> UpdateExcludedClientsAsync([FromBody] UpdateStatsExclusionsRequest request)
    {
        // Prefer mode-aware rules when provided; fall back to the legacy ips-only payload.
        if (request.Rules != null)
        {
            var normalizedRules = NormalizeClientRules(request.Rules, out var invalidRuleIps);
            if (invalidRuleIps.Count > 0)
            {
                return BadRequest(new
                {
                    error = "Invalid exclusion rules",
                    message = "One or more exclusions are not valid. Please correct them and try again.",
                    invalidIps = invalidRuleIps,
                });
            }

            _stateRepository.SetExcludedClientRules(normalizedRules);
        }
        else
        {
            var normalizedIps = NormalizeClientIps(request.Ips, out var invalidIps);
            if (invalidIps.Count > 0)
            {
                return BadRequest(new
                {
                    error = "Invalid exclusion rules",
                    message = "One or more exclusions are not valid. Please correct them and try again.",
                    invalidIps,
                });
            }

            _stateRepository.SetExcludedClientIps(normalizedIps);
        }

        // Notify clients to refresh downloads/stats since exclusions affect all tabs
        await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
        {
            reason = "exclusions-updated"
        });
        return Ok(new StatsExclusionsResponse
        {
            Ips = _stateRepository.GetStatsExcludedOnlyClientIps(),
            Rules = _stateRepository.GetExcludedClientRules()
        });
    }

    /// <summary>
    /// Gets the current eviction settings.
    /// </summary>
    /// <remarks>
    /// Includes the evicted-data display mode, whether eviction scans notify, and whether
    /// orphaned downloads are pruned.
    /// </remarks>
    [HttpGet("eviction")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(EvictionSettingsResponse), StatusCodes.Status200OK)]
    public ActionResult<EvictionSettingsResponse> GetEvictionSettings()
    {
        var evictedDataMode = _stateRepository.GetEvictedDataMode();
        var evictionScanNotifications = _stateRepository.GetEvictionScanNotifications();
        var pruneOrphanedDownloads = _stateRepository.GetPruneOrphanedDownloads();
        return Ok(new EvictionSettingsResponse
        {
            EvictedDataMode = evictedDataMode,
            EvictionScanNotifications = evictionScanNotifications,
            PruneOrphanedDownloads = pruneOrphanedDownloads
        });
    }

    /// <summary>
    /// Updates the eviction settings.
    /// </summary>
    /// <remarks>
    /// EvictedDataMode is optional so the Schedules page can save just the toggles without knowing
    /// (or touching) the current display mode. Switching to Remove mode auto-starts a destructive
    /// bulk eviction removal, parked in the wait-queue on conflict rather than rejected outright.
    /// </remarks>
    [HttpPut("eviction")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(EvictionSettingsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EvictionSettingsResponse>> UpdateEvictionSettingsAsync([FromBody] UpdateEvictionSettingsRequest request)
    {
        // EvictedDataMode is optional: the Schedules page saves only the toggles and
        // must not touch (or have to know) the current display mode.
        var validModes = new[] { EvictedDataMode.Show.ToWireString(), EvictedDataMode.ShowClean.ToWireString(), EvictedDataMode.Hide.ToWireString(), EvictedDataMode.Remove.ToWireString() };
        if (!string.IsNullOrEmpty(request.EvictedDataMode) && !validModes.Contains(request.EvictedDataMode))
        {
            return BadRequest(new
            {
                error = "Invalid eviction mode",
                message = $"Mode must be one of: {string.Join(", ", validModes)}",
            });
        }

        // Remove mode auto-starts a destructive, key-recipe-dependent eviction removal;
        // reject it up front when any datasource's cache keys cannot be computed.
        if (request.EvictedDataMode == EvictedDataMode.Remove.ToWireString())
        {
            var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
            if (capabilityDenial != null)
            {
                return BadRequest(ApiResponse.Error(capabilityDenial));
            }
        }

        if (!string.IsNullOrEmpty(request.EvictedDataMode))
        {
            _stateRepository.SetEvictedDataMode(request.EvictedDataMode);
        }
        if (request.EvictionScanNotifications.HasValue)
        {
            // Keep the legacy boolean written for old readers/migration.
            _stateRepository.SetEvictionScanNotifications(request.EvictionScanNotifications.Value);
            // Route the mode change through the registry (not a direct state write) so the LIVE
            // reconciliation instance's EffectiveNotificationMode updates immediately AND persists,
            // exactly like the per-service Schedules page control. A direct state write would only
            // update GET responses and take effect on next restart.
            _scheduleRegistry.SetNotificationMode(
                "cacheReconciliation",
                request.EvictionScanNotifications.Value ? NotificationMode.All : NotificationMode.Manual);
            // Broadcast the schedule list so the Schedules card reflects the new mode without a reload,
            // matching how the per-service control surfaces the change.
            _scheduleRegistry.NotifySchedulesChanged();
        }
        if (request.PruneOrphanedDownloads.HasValue)
        {
            _stateRepository.SetPruneOrphanedDownloads(request.PruneOrphanedDownloads.Value);
        }
        // Notify clients to refresh downloads/stats since eviction mode affects all tabs
        await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
        {
            reason = "eviction-updated"
        });

        if (request.EvictedDataMode == EvictedDataMode.Remove.ToWireString())
        {
            // Wait-queue model: a conflicting bulk eviction removal is parked, never 409'd.
            // CancellationToken.None in the closure: at promotion the HTTP request is gone.
            async Task<Guid?> StartBulkRemovalAsync() =>
                await _reconciliationService.StartBulkEvictionRemovalAsync(CancellationToken.None);

            var conflict = await _conflictChecker.CheckAsync(
                OperationType.EvictionRemoval,
                ConflictScope.Bulk(),
                HttpContext.RequestAborted);
            if (conflict != null)
            {
                var queuedOutcome = await _operationQueue.EnqueueAsync(
                    OperationType.EvictionRemoval, ConflictScope.Bulk(), "Evicted Data Removal (all)",
                    StartBulkRemovalAsync, HttpContext.RequestAborted);
                return Accepted(new
                {
                    evictedDataMode = _stateRepository.GetEvictedDataMode(),
                    evictionScanNotifications = _stateRepository.GetEvictionScanNotifications(),
                    pruneOrphanedDownloads = _stateRepository.GetPruneOrphanedDownloads(),
                    operationId = (Guid?)queuedOutcome.OperationId,
                    queued = queuedOutcome.Queued
                });
            }

            var operationId = await _reconciliationService.StartBulkEvictionRemovalAsync(HttpContext.RequestAborted);

            return Accepted(new { evictedDataMode = _stateRepository.GetEvictedDataMode(), evictionScanNotifications = _stateRepository.GetEvictionScanNotifications(), pruneOrphanedDownloads = _stateRepository.GetPruneOrphanedDownloads(), operationId });
        }

        return Ok(new EvictionSettingsResponse
        {
            EvictedDataMode = _stateRepository.GetEvictedDataMode(),
            EvictionScanNotifications = _stateRepository.GetEvictionScanNotifications(),
            PruneOrphanedDownloads = _stateRepository.GetPruneOrphanedDownloads()
        });
    }

    /// <summary>
    /// Manually starts an eviction scan.
    /// </summary>
    /// <remarks>
    /// Requires every datasource to have one unambiguous cache-key scheme, since the scan needs it
    /// to identify evictable entries. A conflicting scan is parked in the wait-queue rather than rejected.
    /// </remarks>
    [HttpPost("eviction/reconcile")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(EvictionScanStartedResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EvictionScanStartedResponse>> ReconcileAsync(CancellationToken cancellationToken)
    {
        // The eviction scan requires one unambiguous key scheme across every datasource;
        // reject mixed or unknown evidence before the scan, which also revalidates.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            return BadRequest(ApiResponse.Error(capabilityDenial));
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        Task<Guid?> StartManualScanAsync() => Task.FromResult(_reconciliationService.RunManualAsync());

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.EvictionScan,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
                StartManualScanAsync, cancellationToken));
        }

        var operationId = _reconciliationService.RunManualAsync();
        if (operationId == null)
        {
            // Race: scan started between our check and RunManualAsync - park it.
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan",
                StartManualScanAsync, cancellationToken));
        }

        return Ok(new EvictionScanStartedResponse { OperationId = operationId });
    }

    /// <summary>
    /// Clears the evicted flag on every download.
    /// </summary>
    /// <remarks>
    /// Undoes the effect of eviction removal without touching the cache itself.
    /// </remarks>
    [HttpPost("eviction/reset")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(EvictionResetResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<EvictionResetResponse>> ResetEvictionsAsync(CancellationToken ct)
    {
        var resetCount = await _context.Downloads
            .Where(d => d.IsEvicted)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsEvicted, false), ct);

        await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new { reason = "eviction-reset" });
        return Ok(new EvictionResetResponse { Reset = resetCount });
    }

    /// <summary>
    /// Gets the running eviction scan's progress.
    /// </summary>
    /// <remarks>
    /// Returns an idle/completed snapshot when none is running. Replaces the old polling-status
    /// pattern for every other surface; this one is kept because the Schedules page still polls it directly.
    /// </remarks>
    [HttpGet("eviction/scan/status")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(EvictionScanStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<EvictionScanStatusResponse> EvictionScanStatus()
    {
        var activeScan = _operationTracker.GetActiveOperations(OperationType.EvictionScan).FirstOrDefault();
        var silentMode = activeScan != null ? _reconciliationService.CurrentScanIsSilent : false;
        if (activeScan == null)
        {
            return Ok(new EvictionScanStatusResponse
            {
                IsProcessing = false,
                SilentMode = silentMode,
                // Display flag mirror of silentMode: the recovery config skips resurrecting a card
                // whose run is display-silent (scanSilent). No scan active → nothing to skip.
                ShowNotification = !silentMode,
                Status = OperationStatus.Completed,
                PercentComplete = 0.0,
                Message = string.Empty,
                // StageKey/Context mirror the SignalR EvictionScanProgress shape so the recovery
                // config (RECOVERY_CONFIGS.evictionScan.createNotification) can render the live stage
                // label instead of the generic "Scanning..." fallback. No scan active → null.
                StageKey = null,
                Context = null,
                OperationId = null
            });
        }

        // UpdateProgress stores the current progress stage key in Message (see the scan progress
        // monitor in CacheReconciliationService), so it doubles as the i18n stageKey the frontend
        // recovery card interpolates. The tracker's OperationInfo carries no context dictionary, so
        // the reconciliation service exposes the latest progress context (totalProcessed/
        // totalEstimate) for placeholder-bearing keys like signalr.evictionScan.progress.
        var stageKey = string.IsNullOrWhiteSpace(activeScan.Message) ? null : activeScan.Message;

        return Ok(new EvictionScanStatusResponse
        {
            IsProcessing = true,
            SilentMode = silentMode,
            // Display flag mirror of silentMode so a refresh cannot resurrect a silent run's card.
            ShowNotification = !silentMode,
            Status = activeScan.Status,
            PercentComplete = activeScan.PercentComplete,
            Message = stageKey ?? "Scanning for evictable cache entries...",
            StageKey = stageKey,
            Context = _reconciliationService.CurrentScanProgressContext,
            OperationId = activeScan.Id
        });
    }

    /// <summary>
    /// Gets per-service traffic totals for the given period.
    /// </summary>
    /// <remarks>
    /// Aggregated live from Downloads, not the cached ServiceStats table, which caused values to
    /// fluctuate against the dashboard.
    /// </remarks>
    [HttpGet("services")]
    [ProducesResponseType(typeof(List<ServiceStats>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ServiceStats>>> GetServicesAsync([FromQuery] string? since = null, [FromQuery] long? startTime = null, [FromQuery] long? endTime = null, [FromQuery] long? eventId = null, CancellationToken ct = default)
    {
        // Parse event IDs
        var eventIdList = ParseEventId(eventId);

        // A cascade delete removes the event's EventDownloads rows, so an unknown id would
        // otherwise flow through ApplyEventFilter as an empty (but 200 OK) result instead of
        // a clear signal that the id is gone.
        if (eventId.HasValue)
        {
            await _eventsService.GetByIdOrThrowAsync(eventId.Value, "Event", ct);
        }

        // ALWAYS query Downloads table directly to ensure consistency with dashboard stats
        // Previously used cached ServiceStats table which caused fluctuating values
        // Filter out hidden IPs completely, but include excluded IPs (they'll be excluded from calculations)
        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();
        var query = BaseDownloadsQuery(hiddenClientIps, evictedMode);

        // Apply event filter if provided (filters to only tagged downloads)
        HashSet<long>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
        query = query.ApplyEventFilter(eventIdList, eventDownloadIds);

        // Apply time filtering if provided
        if (startTime.HasValue)
        {
            var startDate = startTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc >= startDate);
        }
        if (endTime.HasValue)
        {
            var endDate = endTime.Value.FromUnixSeconds();
            query = query.Where(d => d.StartTimeUtc <= endDate);
        }
        else if (!string.IsNullOrEmpty(since) && since != "all")
        {
            // Parse time period string for backwards compatibility
            var cutoffTime = TimeUtils.ParseTimePeriod(since);
            if (cutoffTime.HasValue)
            {
                query = query.Where(d => d.StartTimeUtc >= cutoffTime.Value);
            }
        }
        // No filter = all data (consistent with dashboard)

        // Aggregate by service from Downloads table (exclude stats-excluded IPs from calculations)
        var serviceStatsQuery = statsExcludedOnlyIps.Count > 0
            ? query.Where(d => !statsExcludedOnlyIps.Contains(d.ClientIp))
            : query;
        var serviceStats = await DashboardBatchService
            .ServiceStatsQuery(serviceStatsQuery)
            .ToListAsync();

        return Ok(serviceStats.WithUtcMarking());
    }

    /// <summary>
    /// Gets the dashboard summary.
    /// </summary>
    /// <remarks>
    /// Includes all-time totals, current status, and period-specific metrics plus a per-service
    /// breakdown for the requested window. All-time totals ignore the event filter, so they always
    /// represent overall system stats regardless of which event is selected.
    /// </remarks>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(DashboardStatsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DashboardStatsResponse>> DashboardStatsAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null,
        [FromQuery] long? eventId = null,
        CancellationToken ct = default)
    {
        // Parse event IDs
        var eventIdList = ParseEventId(eventId);

        // A cascade delete removes the event's EventDownloads rows, so an unknown id would
        // otherwise flow through ApplyEventFilter as an empty (but 200 OK) result instead of
        // a clear signal that the id is gone.
        if (eventId.HasValue)
        {
            await _eventsService.GetByIdOrThrowAsync(eventId.Value, "Event", ct);
        }

        var hiddenClientIps = _stateRepository.GetHiddenClientIps();
        var statsExcludedOnlyIps = _stateRepository.GetStatsExcludedOnlyClientIps();
        var evictedMode = _stateRepository.GetEvictedDataMode();

        // Use Unix timestamps if provided, otherwise return ALL data (no time filter)
        // This ensures consistency: frontend always provides timestamps for time-filtered queries
        DateTime? cutoffTime = null;
        DateTime? endDateTime = null;

        if (startTime.HasValue)
        {
            cutoffTime = startTime.Value.FromUnixSeconds();
        }
        if (endTime.HasValue)
        {
            endDateTime = endTime.Value.FromUnixSeconds();
        }
        // If no timestamps provided, cutoffTime and endDateTime remain null = query ALL data

        // IMPORTANT: Calculate ALL metrics from Downloads table directly (no cache)
        // This ensures consistency - mixing cached ServiceStats with live Downloads caused fluctuating values

        // Build the base query for period-specific metrics
        // Filter out hidden IPs completely, but include excluded IPs (they'll be excluded from calculations)
        var downloadsQuery = BaseDownloadsQuery(hiddenClientIps, evictedMode);

        // Apply event filter if provided (filters to only tagged downloads)
        HashSet<long>? eventDownloadIds = eventIdList.Count > 0 ? await GetEventDownloadIdsAsync(eventIdList) : null;
        downloadsQuery = downloadsQuery.ApplyEventFilter(eventIdList, eventDownloadIds);

        if (cutoffTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc >= cutoffTime.Value);
        }
        if (endDateTime.HasValue)
        {
            downloadsQuery = downloadsQuery.Where(d => d.StartTimeUtc <= endDateTime.Value);
        }

        // Calculate ALL-TIME totals from Downloads table directly (no cache)
        // Note: All-time totals should NOT be filtered by event - they represent overall system stats
        // Filter out hidden IPs, but exclude stats-excluded IPs from calculations
        var allTimeQuery = BaseDownloadsQuery(hiddenClientIps, evictedMode);
        var totalHitBytes = await AggregateExcludingAsync(allTimeQuery, statsExcludedOnlyIps,
            q => q.SumAsync(d => d.CacheHitBytes));
        var totalMissBytes = await AggregateExcludingAsync(allTimeQuery, statsExcludedOnlyIps,
            q => q.SumAsync(d => d.CacheMissBytes));

        // Calculate PERIOD-specific metrics (exclude stats-excluded IPs from calculations)
        var periodHitBytes = await AggregateExcludingAsync(downloadsQuery, statsExcludedOnlyIps,
            q => q.SumAsync(d => d.CacheHitBytes));
        var periodMissBytes = await AggregateExcludingAsync(downloadsQuery, statsExcludedOnlyIps,
            q => q.SumAsync(d => d.CacheMissBytes));
        var periodDownloadCount = await AggregateExcludingAsync(downloadsQuery, statsExcludedOnlyIps,
            q => q.CountAsync());

        // Get top service from Downloads table (not cached ServiceStats)
        // Exclude stats-excluded IPs from the sum calculation
        var topServiceQuery = BaseDownloadsQuery(hiddenClientIps, evictedMode);
        var topServiceGroups = await topServiceQuery
            .GroupBy(d => d.Service)
            .Select(g => new { Service = g.Key, TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) })
            .ToListAsync();

        // Subtract excluded IPs from each service's total
        if (statsExcludedOnlyIps.Count > 0)
        {
            var excludedServiceGroups = await topServiceQuery
                .Where(d => statsExcludedOnlyIps.Contains(d.ClientIp))
                .GroupBy(d => d.Service)
                .Select(g => new { Service = g.Key, TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) })
                .ToListAsync();
            
            var excludedByService = excludedServiceGroups.ToDictionary(g => g.Service, g => g.TotalBytes);
            topServiceGroups = topServiceGroups
                .Select(g => new { 
                    g.Service, 
                    TotalBytes = excludedByService.TryGetValue(g.Service, out var excludedBytes) 
                        ? g.TotalBytes - excludedBytes 
                        : g.TotalBytes 
                })
                .ToList();
        }
        
        var topService = topServiceGroups
            .OrderByDescending(s => s.TotalBytes)
            .FirstOrDefault();

        // Active downloads and unique clients (exclude stats-excluded IPs from counts)
        var activeDownloadsQuery = BaseDownloadsQuery(hiddenClientIps, evictedMode)
            .Where(d => d.IsActive && d.EndTimeUtc > DateTime.UtcNow.AddMinutes(-5));
        var activeDownloads = await AggregateExcludingAsync(activeDownloadsQuery, statsExcludedOnlyIps,
            q => q.CountAsync());

        // Unique clients: count distinct IPs, excluding stats-excluded IPs
        int uniqueClientsCount;
        if (cutoffTime.HasValue || endDateTime.HasValue)
        {
            // For period queries, count distinct IPs excluding stats-excluded
            var allIps = await downloadsQuery.Select(d => d.ClientIp).Distinct().ToListAsync();
            var excludedCount = statsExcludedOnlyIps.Count > 0 
                ? allIps.Count(ip => statsExcludedOnlyIps.Contains(ip))
                : 0;
            uniqueClientsCount = allIps.Count - excludedCount;
        }
        else
        {
            // For all-time, count distinct IPs excluding stats-excluded
            var allIps = await BaseDownloadsQuery(hiddenClientIps, evictedMode)
                .Select(d => d.ClientIp)
                .Distinct()
                .ToListAsync();
            var excludedCount = statsExcludedOnlyIps.Count > 0 
                ? allIps.Count(ip => statsExcludedOnlyIps.Contains(ip))
                : 0;
            uniqueClientsCount = allIps.Count - excludedCount;
        }

        // All-time metrics (from Downloads table directly)
        var totalBandwidthSaved = totalHitBytes;
        var totalAddedToCache = totalMissBytes;
        var totalServed = totalBandwidthSaved + totalAddedToCache;
        var cacheHitRatio = totalServed > 0
            ? (double)totalBandwidthSaved / totalServed
            : 0;
        var topServiceName = topService?.Service ?? "none";

        // Period-specific metrics
        var periodTotal = periodHitBytes + periodMissBytes;
        var periodHitRatio = periodTotal > 0
            ? (double)periodHitBytes / periodTotal
            : 0;

        // Determine period label for response
        string periodLabel = "all";
        if (cutoffTime.HasValue && endDateTime.HasValue)
        {
            var duration = endDateTime.Value - cutoffTime.Value;
            periodLabel = duration.TotalHours <= 24 ? $"{(int)duration.TotalHours}h" : $"{(int)duration.TotalDays}d";
        }
        else if (cutoffTime.HasValue)
        {
            periodLabel = "since " + cutoffTime.Value.ToString("yyyy-MM-dd");
        }

        // xboxlive and microsoft rows are folded into xbox after materialisation
        var serviceBreakdown = ServiceBreakdownMerger.MergeXboxRows(await downloadsQuery
            .GroupBy(d => d.Service)
            .Select(g => new ServiceBreakdownItem
            {
                Service = g.Key,
                Bytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                Percentage = periodTotal > 0
                    ? (g.Sum(d => d.CacheHitBytes + d.CacheMissBytes) * 100.0) / periodTotal
                    : 0
            })
            .OrderByDescending(s => s.Bytes)
            .ToListAsync());

        return Ok(new DashboardStatsResponse
        {
            // All-time metrics (always from ServiceStats totals)
            TotalBandwidthSaved = totalBandwidthSaved,
            TotalAddedToCache = totalAddedToCache,
            TotalServed = totalServed,
            CacheHitRatio = cacheHitRatio,

            // Current status
            ActiveDownloads = activeDownloads,
            UniqueClients = uniqueClientsCount,
            TopService = topServiceName,

            // Period-specific metrics
            Period = new DashboardPeriodStats
            {
                Duration = periodLabel,
                Since = cutoffTime,
                BandwidthSaved = periodHitBytes,
                AddedToCache = periodMissBytes,
                TotalServed = periodTotal,
                HitRatio = periodHitRatio,
                Downloads = periodDownloadCount
            },

            // Service breakdown (uses period-filtered query for consistency, including event filter if provided)
            ServiceBreakdown = serviceBreakdown,

            LastUpdated = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Gets the historical cache size snapshot for a time range.
    /// </summary>
    /// <remarks>
    /// Returns estimated used space based on periodic snapshots.
    /// </remarks>
    [HttpGet("cache-snapshot")]
    [ProducesResponseType(typeof(CacheSnapshotResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<CacheSnapshotResponse>> CacheSnapshotAsync(
        [FromQuery] long? startTime = null,
        [FromQuery] long? endTime = null)
    {
        if (!startTime.HasValue || !endTime.HasValue)
        {
            return Ok(new CacheSnapshotResponse { HasData = false });
        }

        var startUtc = startTime.Value.FromUnixSeconds();
        var endUtc = endTime.Value.FromUnixSeconds();

        var summary = await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc);

        if (summary == null)
        {
            return Ok(new CacheSnapshotResponse { HasData = false });
        }

        return Ok(new CacheSnapshotResponse
        {
            HasData = true,
            StartUsedSize = summary.StartUsedSize,
            EndUsedSize = summary.EndUsedSize,
            AverageUsedSize = summary.AverageUsedSize,
            TotalCacheSize = summary.TotalCacheSize,
            SnapshotCount = summary.SnapshotCount,
            IsEstimate = summary.IsEstimate
        });
    }
}
