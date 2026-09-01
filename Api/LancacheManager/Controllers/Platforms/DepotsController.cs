using LancacheManager.Core.Interfaces;
using System.Text.Json;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;


namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for depot mapping management
/// Handles Steam PICS data, depot rebuilds, imports, and mappings
/// Note: /steamkit/* routes have been renamed to /depots/* for proper resource-based naming
/// Note: crawl interval and scan mode are configured here, under /api/depots/rebuild/config/*
/// </summary>
[ApiController]
[Route("api/depots")]
[Authorize]
public class DepotsController : ControllerBase
{
    private readonly SteamKit2Service _steamKit2Service;
    private readonly PicsDataService _picsDataService;
    private readonly StateService _stateService;
    private readonly ILogger<DepotsController> _logger;
    private readonly IOperationConflictChecker _conflictChecker;

    public DepotsController(
        SteamKit2Service steamKit2Service,
        PicsDataService picsDataService,
        StateService stateService,
        ILogger<DepotsController> logger,
        IOperationConflictChecker conflictChecker)
    {
        _steamKit2Service = steamKit2Service;
        _picsDataService = picsDataService;
        _stateService = stateService;
        _logger = logger;
        _conflictChecker = conflictChecker;
    }

    /// <summary>
    /// Gets the status of depot mappings from the PICS JSON file and database.
    /// </summary>
    /// <remarks>
    /// This is a proper resource status endpoint.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("status")]
    [Authorize]
    [ProducesResponseType(typeof(DepotFullStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DepotFullStatusResponse>> GetDepotStatusAsync()
    {
        var picsData = await _picsDataService.LoadFromJsonAsync();
        var needsUpdate = await _picsDataService.NeedsUpdateAsync();
        var dbMappingCount = await _steamKit2Service.GetDepotMappingCountAsync();

        return Ok(new DepotFullStatusResponse
        {
            JsonFile = new DepotJsonFileStatus
            {
                Exists = picsData != null,
                Path = _picsDataService.GetPicsJsonFilePath(),
                LastUpdated = picsData?.Metadata?.LastUpdated,
                TotalMappings = picsData?.Metadata?.TotalMappings ?? 0,
                NextUpdateDue = picsData?.Metadata?.NextUpdateDue,
                NeedsUpdate = needsUpdate
            },
            Database = new DepotDatabaseStatus
            {
                TotalMappings = dbMappingCount
            },
            SteamKit2 = new DepotSteamKit2Status
            {
                IsReady = _steamKit2Service.IsReady,
                IsRebuildRunning = _steamKit2Service.IsRebuildRunning,
                DepotCount = dbMappingCount
            }
        });
    }

    /// <summary>
    /// Starts a depot mapping rebuild, optionally incremental.
    /// </summary>
    /// <remarks>
    /// POST is acceptable for starting async operations. This endpoint runs a pre-flight
    /// viability check: if incremental=true, it checks with Steam whether an incremental scan
    /// is viable before starting, and if Steam requires a full scan, it returns the
    /// requiresFullScan flag so a modal can be shown to the user. If incremental=false, the
    /// viability check is skipped and the endpoint proceeds directly to a full scan.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("rebuild")]
    [ProducesResponseType(typeof(DepotRebuildViabilityResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DepotRebuildStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> StartDepotRebuildAsync(CancellationToken cancellationToken, [FromQuery] bool incremental = false)
    {
        // Without this a rebuild already running answers every repeat click with another 202, so a
        // caller that isn't reading RebuildInProgress off the body can't tell a duplicate click from
        // a real new one.
        var conflict = await _conflictChecker.CheckAsync(
            OperationType.DepotMapping,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            return Conflict(conflict);
        }

        // PRE-FLIGHT CHECK: Only check viability if user requested incremental scan
        if (incremental)
        {
            _logger.LogInformation("Incremental scan requested - checking viability first");
            var viability = await _steamKit2Service.CheckViabilityAsync(cancellationToken);
            _logger.LogInformation("Viability check returned: {Viability}", System.Text.Json.JsonSerializer.Serialize(viability));

            if (viability.WillTriggerFullScan)
            {
                _logger.LogInformation("Incremental scan not viable - change gap too large ({ChangeGap}). Returning requiresFullScan flag.", viability.ChangeGap);

                return Ok(new DepotRebuildViabilityResponse
                {
                    Started = false,
                    RequiresFullScan = true,
                    ChangeGap = viability.ChangeGap,
                    EstimatedApps = viability.EstimatedAppsToScan,
                    Message = viability.Error ?? "Change gap is too large for incremental scan. A full scan is required.",
                    ViabilityError = viability.Error
                });
            }
            else
            {
                _logger.LogInformation("Incremental scan is viable - proceeding with scan");
                _steamKit2Service.ClearScanSkippedFlag();
            }
        }

        // Proceed with scan
        var started = _steamKit2Service.TryStartRebuild(cancellationToken, incremental);

        if (started)
        {
            _steamKit2Service.EnablePeriodicCrawls();
        }

        return Accepted(new DepotRebuildStartResponse
        {
            Started = started,
            RequiresFullScan = false,
            RebuildInProgress = _steamKit2Service.IsRebuildRunning,
            Ready = _steamKit2Service.IsReady,
            DepotCount = await _steamKit2Service.GetDepotMappingCountAsync()
        });
    }

    /// <summary>
    /// Gets the current depot rebuild progress.
    /// </summary>
    /// <remarks>
    /// Progress is a sub-resource of the rebuild operation.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("rebuild/progress")]
    [ProducesResponseType(typeof(SteamPicsProgress), StatusCodes.Status200OK)]
    public ActionResult<SteamPicsProgress> GetRebuildProgress()
    {
        var progress = _steamKit2Service.GetProgress();
        return Ok(progress);
    }

    /// <summary>
    /// Cancels the current depot rebuild.
    /// </summary>
    /// <remarks>
    /// DELETE is the proper method for cancelling or removing operations.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("rebuild")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> CancelRebuildAsync()
    {
        var cancelled = await _steamKit2Service.CancelRebuildAsync();

        if (cancelled)
        {
            return Ok(new MessageResponse { Message = "Depot rebuild cancelled successfully" });
        }
        else
        {
            return NotFound(new ErrorResponse
            {
                Error = "No active rebuild to cancel",
                StageKey = "errors.depot.noActiveRebuild"
            });
        }
    }

    /// <summary>
    /// Checks whether an incremental depot scan is viable.
    /// </summary>
    /// <remarks>
    /// This is a query/check operation on the rebuild resource.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpGet("rebuild/check-incremental")]
    [ProducesResponseType(typeof(IncrementalViabilityCheck), StatusCodes.Status200OK)]
    public async Task<ActionResult<IncrementalViabilityCheck>> CheckIncrementalAsync(CancellationToken cancellationToken)
    {
        var result = await _steamKit2Service.CheckViabilityAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Imports depot mappings from GitHub or the local PICS data.
    /// </summary>
    /// <remarks>
    /// POST is the proper method for importing or creating resources. The query parameter
    /// 'source' determines the import source: 'github' or 'local'.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPost("import")]
    [ProducesResponseType(typeof(DepotImportResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DepotImportResponse>> ImportDepotMappingsAsync([FromQuery] string source, CancellationToken cancellationToken)
    {
        if (source == "github")
        {
            // Without this the request is dropped inside the service and still answered with a
            // successful import, so repeat clicks all look like they worked while nothing ran.
            var conflict = await _conflictChecker.CheckAsync(
                OperationType.DepotMapping,
                ConflictScope.Bulk(),
                cancellationToken);
            if (conflict != null)
            {
                return Conflict(conflict);
            }

            // The tracker only learns about a run once it registers, which happens after the
            // service takes its single-run lock. This covers that window.
            if (_steamKit2Service.IsRebuildRunning)
            {
                _logger.LogInformation("Depot download requested while a depot run is already starting");

                return Conflict(new OperationConflictResponse
                {
                    StageKey = "errors.conflict.duplicate",
                    Error = "A DepotMapping operation for the same target is already in progress."
                });
            }

            _logger.LogInformation("Starting download of pre-created depot data from GitHub");

            var success = await _steamKit2Service.ImportFromGitHubAsync(cancellationToken);

            if (success)
            {
                _steamKit2Service.ClearScanSkippedFlag();
                _steamKit2Service.EnablePeriodicCrawls();

                _logger.LogInformation("Pre-created depot data downloaded and imported successfully from GitHub");

                return Ok(new DepotImportResponse
                {
                    Message = "Pre-created depot data downloaded and imported successfully",
                    Source = "GitHub",
                    Timestamp = DateTime.UtcNow
                });
            }
            else
            {
                // The request was fine; the GitHub download or the import of what it returned failed.
                // 503 keeps the reason visible in production, where a 500 would replace it with the
                // generic safe message and leave the admin with nothing to act on.
                throw new ServiceUnavailableException("Failed to download and import pre-created depot data from GitHub")
                {
                    StageKey = "errors.depot.githubImportFailed"
                };
            }
        }
        else if (source == "local")
        {
            _logger.LogInformation("Starting import of existing PICS data to database");

            await _picsDataService.ImportToDatabaseAsync(cancellationToken);
            _steamKit2Service.EnablePeriodicCrawls();

            return Ok(new DepotImportResponse
            {
                Message = "PICS data imported successfully",
                Source = "Local",
                Timestamp = DateTime.UtcNow
            });
        }
        else
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid source. Must be 'github' or 'local'",
                StageKey = "errors.depot.invalidSource"
            });
        }
    }

    /// <summary>
    /// Applies depot mappings to existing downloads.
    /// </summary>
    /// <remarks>
    /// PATCH is the proper method for applying updates to a resource collection.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpPatch]
    [ProducesResponseType(typeof(DepotMappingApplyResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DepotMappingApplyResponse>> ApplyDepotMappingsAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting manual depot mapping application");

        await _steamKit2Service.ManuallyApplyDepotMappingsAsync();

        _logger.LogInformation("Manual depot mapping completed successfully");

        return Ok(new DepotMappingApplyResponse
        {
            Message = "Depot mappings applied successfully",
            Timestamp = DateTime.UtcNow
        });
    }


    /// <summary>
    /// Sets the automatic depot crawl interval.
    /// </summary>
    /// <remarks>
    /// PUT is the proper method for updating configuration.
    /// </remarks>
    /// <param name="intervalHours">Interval in hours (supports fractional values like 0.00833 for 30 seconds). Use 0 to disable.</param>
    [Authorize(Policy = "AccountHolder")]
    [HttpPut("rebuild/config/interval")]
    [ProducesResponseType(typeof(CrawlIntervalResponse), StatusCodes.Status200OK)]
    public ActionResult<CrawlIntervalResponse> SetCrawlInterval([FromBody] double intervalHours)
    {
        _logger.LogInformation("Received crawl interval request: {IntervalHours} hours", intervalHours);

        if (intervalHours < 0)
        {
            return BadRequest(new ErrorResponse
            {
                Error = $"Interval must be 0 or a positive number. Received: {intervalHours}",
                StageKey = "errors.depot.invalidInterval",
                Context = new() { ["received"] = intervalHours }
            });
        }

        _steamKit2Service.CrawlIntervalHours = intervalHours;

        var actualInterval = _steamKit2Service.CrawlIntervalHours;
        _logger.LogInformation("Crawl interval set. Requested: {Requested}, Actual: {Actual}", intervalHours, actualInterval);

        if (intervalHours == 0)
        {
            _logger.LogInformation("Automatic crawl schedule disabled");
        }
        else if (intervalHours < 1)
        {
            var seconds = intervalHours * 3600;
            _logger.LogInformation("Crawl interval updated to {Seconds} seconds", (int)seconds);
        }
        else
        {
            _logger.LogInformation("Crawl interval updated to {IntervalHours} hours", intervalHours);
        }

        return Ok(new CrawlIntervalResponse
        {
            IntervalHours = (int)_steamKit2Service.CrawlIntervalHours,
            Message = intervalHours == 0
                ? "Automatic schedule disabled"
                : intervalHours < 1
                    ? $"Crawl interval updated to {(int)(intervalHours * 3600)} seconds (testing mode)"
                    : $"Crawl interval updated to {intervalHours} hour(s)"
        });
    }

    /// <summary>
    /// Sets the automatic depot crawl mode.
    /// </summary>
    /// <remarks>
    /// PUT is the proper method for updating configuration. A Steam mode is refused when its
    /// requirements are not met, so a direct call, a second tab, or a stale page cannot store a
    /// mode that every scheduled run would have to skip.
    /// </remarks>
    /// <param name="mode">Mode value: true (incremental), false (full), "hybrid" (incremental until the last full crawl is a week old, then one full crawl), or "github" (PICS updates only)</param>
    [Authorize(Policy = "AccountHolder")]
    [HttpPut("rebuild/config/mode")]
    [ProducesResponseType(typeof(CrawlModeResponse), StatusCodes.Status200OK)]
    public ActionResult<CrawlModeResponse> SetCrawlMode([FromBody] JsonElement mode)
    {
        string scanMode;

        // Handle different input types: bool or string "github"
        if (mode.ValueKind == JsonValueKind.True || mode.ValueKind == JsonValueKind.False)
        {
            var incremental = mode.ValueKind == JsonValueKind.True;

            var unavailable = DepotScanModeRequirement.Missing(_steamKit2Service, _stateService, incremental);
            if (unavailable != null)
            {
                _logger.LogInformation("Crawl mode rejected: {StageKey}", unavailable.StageKey);

                return BadRequest(unavailable);
            }

            _steamKit2Service.CrawlIncrementalMode = incremental;
            scanMode = incremental ? "Incremental" : "Full";
        }
        else if (mode.ValueKind == JsonValueKind.String && mode.GetString() == "github")
        {
            _steamKit2Service.CrawlIncrementalMode = "github";
            scanMode = "GitHub (PICS Updates)";
        }
        else if (mode.ValueKind == JsonValueKind.String && mode.GetString() == "hybrid")
        {
            // A hybrid week ends in a full crawl, so it is judged against what a full scan needs.
            // Storing it where a full scan would be refused would put a run that cannot finish on
            // the schedule, and the incremental days do not add a requirement of their own: with no
            // mappings stored the first hybrid run is the full one, which builds the baseline.
            var unavailable = DepotScanModeRequirement.Missing(_steamKit2Service, _stateService, incremental: false);
            if (unavailable != null)
            {
                _logger.LogInformation("Crawl mode rejected: {StageKey}", unavailable.StageKey);

                return BadRequest(unavailable);
            }

            _steamKit2Service.CrawlIncrementalMode = "hybrid";
            scanMode = "Hybrid";
        }
        else
        {
            return BadRequest(new ErrorResponse
            {
                Error = "Invalid scan mode. Must be true, false, \"hybrid\", or \"github\"",
                StageKey = "errors.depot.invalidScanMode"
            });
        }

        _logger.LogInformation("Crawl mode updated to {Mode}", scanMode);

        return Ok(new CrawlModeResponse
        {
            IncrementalMode = _steamKit2Service.CrawlIncrementalMode,
            Message = $"Automatic scan mode set to {scanMode}"
        });
    }
}

/// <summary>
/// The one place that decides what a scheduled depot scan mode needs, so the two routes that store a
/// mode cannot disagree about which modes can run. Every term reads a fact that survives at rest: a
/// crawl closes its own Steam socket when it finishes, so a connection or login flag would refuse
/// both Steam modes permanently. Reachability is deliberately not a term here - it cannot be answered
/// without opening a Steam connection, which a configuration save must never do, and a scheduled run
/// that cannot reach Steam already reports itself as skipped.
/// </summary>
internal static class DepotScanModeRequirement
{
    /// <summary>
    /// Reads the facts off the running services and judges the mode against them. GitHub mode needs
    /// neither Steam nor a key, so it never reaches this.
    /// </summary>
    internal static ScanModeUnavailableResponse? Missing(
        SteamKit2Service steamKit2Service,
        StateService stateService,
        bool incremental)
    {
        var availability = new DepotScanModeAvailability(
            SetupCompleted: stateService.GetSetupCompleted(),
            RebuildRunning: steamKit2Service.IsRebuildRunning,
            DepotMappingsFound: steamKit2Service.GetProgress().DepotMappingsFound,
            WebApiAvailable: steamKit2Service.IsWebApiAvailable());

        return Missing(availability, incremental);
    }

    /// <summary>
    /// The requirement a scan mode is missing, or null when the mode can run. The stage keys are the
    /// ones the scan mode dropdown already shows for the same missing requirement, so a refused save
    /// and a greyed-out option give the user the same sentence.
    /// </summary>
    internal static ScanModeUnavailableResponse? Missing(DepotScanModeAvailability availability, bool incremental)
    {
        if (!availability.SetupCompleted)
        {
            return new ScanModeUnavailableResponse
            {
                StageKey = "management.depotMapping.modes.setupRequiredHelp",
                Error = "Finish setup before scheduling a Steam depot scan."
            };
        }

        if (incremental)
        {
            // An incremental scan asks Steam what changed since the mappings already stored, so with
            // none stored there is nothing to compare against. A crawl that is still running has not
            // written all of its mappings yet, so a count read mid-crawl says nothing about whether a
            // baseline exists by the time this mode is used. No crawl empties the table: mappings are
            // only ever inserted or updated in place, and the one route that deletes them is the
            // GitHub import replacing the whole set.
            if (availability.RebuildRunning || availability.DepotMappingsFound > 0)
            {
                return null;
            }

            return new ScanModeUnavailableResponse
            {
                StageKey = "management.depotMapping.modes.mappingsRequiredHelp",
                Error = "No depot mappings are stored yet. Run a full scan or import from GitHub first."
            };
        }

        // A full scan enumerates every app through the Steam Web API. Incremental reads the PICS
        // changelist over the client connection and never calls it, which is why the key is only a
        // requirement on this branch.
        if (availability.WebApiAvailable)
        {
            return null;
        }

        return new ScanModeUnavailableResponse
        {
            StageKey = "management.depotMapping.modes.fullWebApiRequiredHelp",
            Error = "A full scan needs a Steam Web API key."
        };
    }
}

/// <summary>
/// What a scheduled scan mode is judged against: the facts about this install that survive at rest,
/// with no Steam session open. These are the same facts the scan mode dropdown reads in the browser,
/// so the two gates answer from the same evidence.
/// </summary>
internal readonly record struct DepotScanModeAvailability(
    bool SetupCompleted,
    bool RebuildRunning,
    int DepotMappingsFound,
    bool WebApiAvailable);

/// <summary>
/// Refusal body for a scheduled scan mode that cannot run with the depot data and credentials on
/// hand. <see cref="StageKey"/> is the i18n key the client renders; <see cref="Error"/> is the
/// English fallback for a client that does not localize.
/// </summary>
public class ScanModeUnavailableResponse
{
    public string StageKey { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
