using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services;

/// <summary>
/// Background service that runs the Rust speed tracker executable and broadcasts
/// speed snapshots via SignalR. Uses Rust for faster log parsing.
/// </summary>
public class RustSpeedTrackerService : ScheduledBackgroundService
{
    private readonly IPathResolver _pathResolver;
    private readonly DatasourceService _datasourceService;
    private readonly ISignalRNotificationService _notifications;
    private readonly ProcessManager _processManager;
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly IStateService _stateService;
    private readonly IActivityRegistry? _activityRegistry;
    private bool _loggedNoTrackableDatasources;
    private string? _rustExecutablePath;
    private Process? _rustProcess;
    // Raw tracker output, kept private so diagnostics can still inspect actual tracker state.
    // Everything user-facing (REST + SignalR) goes through BuildClientVisibleSnapshot so hidden
    // clients cannot leak through either transport. Prefill traffic is NOT excluded: nothing in
    // the builder or the Rust tracker filters it, so it appears like any other client unless an
    // operator hides its address by hand.
    // Initial value before the first Rust snapshot arrives. Two seconds is the minimum/default
    // window; the Rust tracker reports a window that adapts upward from there toward the
    // observed log-delivery cadence.
    private DownloadSpeedSnapshot _currentSnapshot = new() { WindowSeconds = 2 };
    private readonly object _snapshotLock = new();
    private bool _previousHadActivity = false;
    // Tracks the same edge as _previousHadActivity but over the unfiltered set, so the end of the
    // last download is reported even when the only client downloading was a hidden one.
    private bool _previousHadUnfilteredActivity = false;

    /// <summary>
    /// Supplies the current scan-refusal reason, or null when a scan may start. Set once at
    /// startup by <c>CacheScanGate</c>, which owns the rule; the tracker only needs to know when
    /// the answer changes so it can announce it, and asking through a hook keeps the rule in one
    /// place without the tracker taking a dependency on something that depends on the tracker.
    /// </summary>
    public static Func<string?>? ScanBlockedAnswer { get; set; }

    /// <summary>
    /// How long after the tracker stops reporting the answer above changes on its own, with no
    /// output from the tracker to prompt a re-read. Set alongside <see cref="ScanBlockedAnswer"/>
    /// by the same owner, because the window it clears is part of the rule.
    /// </summary>
    public static TimeSpan ScanBlockedRecheckDelay { get; set; }

    // Last announced answer, so the announcement fires on a change rather than on every tick.
    private bool _previouslyScanBlocked;

    // Serializes reading the answer and recording it in AnnounceScanBlockedIfChangedAsync. The
    // timed announcement runs on its own task, so it can reach that pair at the same moment as the
    // stdout thread, and an interleave there leaves the recorded answer disagreeing with the last
    // one sent, which is how a later real change stops being announced. Taken before _snapshotLock
    // and never the other way round: both writers release _snapshotLock before they announce.
    private readonly object _scanBlockedLock = new();

    /// <summary>
    /// Raised once when the tracker parses a snapshot in which nothing is downloading any more.
    /// Carries nothing: the edge itself is the whole signal.
    /// </summary>
    /// <remarks>
    /// Raised only from a parsed snapshot, never when the tracker process dies. A dead tracker has
    /// stopped answering, which is not the same event as the last download finishing, and treating
    /// the two alike is the confusion the readiness clock exists to prevent.
    /// Handlers run on the tracker's stdout thread and must not throw.
    /// </remarks>
    public static event Action? DownloadsEnded;
    // An empty snapshot means two different things: the tracker looked and saw nothing, or it has
    // no answer to give. This holds the moment the second state began, and is null while the
    // tracker is publishing. Every transition into having no answer sets it: construction, each
    // spawn of the child, and each death of the child. Only a parsed snapshot clears it.
    private DateTime? _unreportedSinceUtc = DateTime.UtcNow;

    // Ceiling for the restart backoff. A dependency the tracker can never satisfy (an unreachable
    // database, a missing log source) stops costing a spawn every few seconds once the delay
    // reaches this, while a dependency that comes back is still picked up within five minutes.
    private static readonly TimeSpan _maxRestartDelay = TimeSpan.FromMinutes(5);

    // A tracker that stayed up this long did real work, so the next exit starts the backoff over
    // rather than inheriting a streak from an unrelated failure hours earlier.
    private static readonly TimeSpan _healthyRunDuration = TimeSpan.FromMinutes(1);

    protected override string ServiceName => "RustSpeedTrackerService";
    // Differs from the base default deliberately: this tracker produces the download signal that
    // gates every cache scan, so until it publishes, a scan cannot tell whether the cache is being
    // written to. It should begin publishing as early as it can rather than inherit a
    // general-purpose settling delay.
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override TimeSpan Interval => TimeSpan.Zero;
    protected override TimeSpan ErrorRetryDelay => TimeSpan.FromSeconds(5);

    public RustSpeedTrackerService(
        ILogger<RustSpeedTrackerService> logger,
        IConfiguration configuration,
        IPathResolver pathResolver,
        DatasourceService datasourceService,
        ISignalRNotificationService notifications,
        ProcessManager processManager,
        DatasourceCapabilityService capabilityService,
        IStateService stateService,
        IActivityRegistry? activityRegistry = null)
        : base(logger, configuration)
    {
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _notifications = notifications;
        _processManager = processManager;
        _capabilityService = capabilityService;
        _stateService = stateService;
        _activityRegistry = activityRegistry;
    }

    /// <summary>
    /// Gets the current CLIENT-VISIBLE speed snapshot: hidden clients are filtered out and the
    /// evicted-data display mode is applied, exactly as the SignalR broadcast does, so REST and
    /// SignalR always expose identical visibility semantics.
    /// </summary>
    public DownloadSpeedSnapshot GetCurrentSnapshot()
    {
        DownloadSpeedSnapshot raw;
        lock (_snapshotLock)
        {
            raw = _currentSnapshot;
        }

        return BuildClientVisibleSnapshot(
            raw, _stateService.GetHiddenClientIps(), _stateService.GetEvictedDataMode());
    }

    /// <summary>
    /// Announces that the answer to "would a cache scan be refused right now" has changed, and
    /// only then. Asking the gate here, rather than deriving the answer from a download edge,
    /// covers the second reason it moves: the tracker gaining or losing the ability to report,
    /// which a download edge misses entirely and which left the scan controls disabled after the
    /// gate had gone idle.
    /// </summary>
    /// <remarks>
    /// A caller is still needed for every way the answer can move. The third way produces no
    /// output at all to hang a call off, because the answer goes from refuse to allow purely
    /// because the no-answer window expires; that one is
    /// <see cref="AnnounceScanBlockedWhenWindowExpiresAsync"/>.
    /// </remarks>
    private Task AnnounceScanBlockedIfChangedAsync()
    {
        lock (_scanBlockedLock)
        {
            var blocked = ScanBlockedAnswer?.Invoke() != null;
            if (blocked == _previouslyScanBlocked)
            {
                return Task.CompletedTask;
            }

            _previouslyScanBlocked = blocked;
        }

        return _notifications.NotifyAllAsync(SignalREvents.CacheScanBlockedChanged, null);
    }

    /// <summary>
    /// Waits out the window during which the tracker having no answer refuses scans, then asks
    /// once more. Nothing the tracker does marks the end of that window: the gate answers from the
    /// clock, so it starts allowing scans at a moment no spawn, no parsed line and no death lines
    /// up with. Without this the last announcement stays "blocked" until the tracker publishes or
    /// spawns again, which for a child that dies into a growing restart delay is minutes and for
    /// one that hangs without printing is forever, leaving the scan buttons disabled while the
    /// server would accept a scan.
    /// </summary>
    /// <remarks>
    /// One wait per arming of the clock rather than a running timer, so a server with nothing
    /// happening stays silent. Announcing is a no-op unless the answer moved, so an arming that
    /// the tracker publishes through before the wait ends costs one comparison.
    /// </remarks>
    internal async Task AnnounceScanBlockedWhenWindowExpiresAsync(CancellationToken stoppingToken)
    {
        await SafeDelayAsync(ScanBlockedRecheckDelay, stoppingToken);

        if (!stoppingToken.IsCancellationRequested)
        {
            await AnnounceScanBlockedIfChangedAsync();
        }
    }

    /// <summary>
    /// Raises <see cref="DownloadsEnded"/> on the snapshot where the download set goes from busy
    /// to idle, then records the new state for the next snapshot to compare against.
    /// </summary>
    /// <remarks>
    /// Reads the UNFILTERED set, not the client-visible projection: hiding a client stops it
    /// appearing on the dashboard, it does not stop its bytes reaching the cache, so the visible
    /// edge can arrive while a hidden download is still running.
    /// </remarks>
    internal void AnnounceDownloadsEndedIfStopped(DownloadSpeedSnapshot snapshot)
    {
        var unfilteredHasActivity = snapshot.HasActiveDownloads;
        if (_previousHadUnfilteredActivity && !unfilteredHasActivity)
        {
            DownloadsEnded?.Invoke();
        }

        _previousHadUnfilteredActivity = unfilteredHasActivity;
    }

    /// <summary>
    /// Reads the UNFILTERED speed snapshot together with the moment the tracker last had no answer
    /// to give, which is null while it is publishing. Unfiltered because bytes reaching the cache
    /// do not stop reaching it when an operator hides the client that is sending them, so anything
    /// deciding whether the cache is being written to reads this rather than the client-visible
    /// projection. While the clock is set, the snapshot is an empty placeholder that says nothing
    /// about what is downloading, so a caller reading it as "quiet" would be reading its own
    /// ignorance.
    /// </summary>
    /// <remarks>
    /// The two are returned from one lock because both transitions write them together. Taken as
    /// two reads, a tracker that died in between hands back the null clock from before the death
    /// and the emptied snapshot from after it, which reads as "nothing is downloading" and lets a
    /// scan start against a tracker that has just stopped answering.
    /// </remarks>
    public (DateTime? UnreportedSinceUtc, DownloadSpeedSnapshot Snapshot) ReadUnfilteredState()
    {
        lock (_snapshotLock)
        {
            return (_unreportedSinceUtc, _currentSnapshot);
        }
    }

    /// <summary>
    /// Builds the client-visible snapshot from the raw tracker snapshot. Hidden clients (the same
    /// exclusion the dashboard applies to recorded downloads) are removed, the evicted-data
    /// display mode is applied, and the top-level totals are recomputed from the retained
    /// entries. Retained game entries are copied so display rewrites (ShowClean) can never
    /// mutate the tracker's raw snapshot.
    /// </summary>
    public static DownloadSpeedSnapshot BuildClientVisibleSnapshot(
        DownloadSpeedSnapshot snapshot,
        IReadOnlyCollection<string> hiddenClientIps,
        string evictedMode)
    {
        var filteredClients = snapshot.ClientSpeeds
            .Where(c => IsVisibleClient(c.ClientIp, hiddenClientIps))
            .ToList();

        var filteredGames = snapshot.GameSpeeds
            .Where(g => string.IsNullOrWhiteSpace(g.ClientIp) || IsVisibleClient(g.ClientIp, hiddenClientIps))
            .Select(CloneGameSpeed)
            .ToList();

        if (evictedMode == EvictedDataMode.Hide.ToWireString() ||
            evictedMode == EvictedDataMode.Remove.ToWireString())
        {
            filteredGames = filteredGames.Where(g => !g.IsEvicted).ToList();
        }
        else if (evictedMode == EvictedDataMode.ShowClean.ToWireString())
        {
            foreach (var g in filteredGames)
            {
                g.IsEvicted = false;
            }
        }

        return new DownloadSpeedSnapshot
        {
            TimestampUtc = snapshot.TimestampUtc,
            WindowSeconds = snapshot.WindowSeconds,
            TotalBytesPerSecond = filteredClients.Sum(c => c.BytesPerSecond),
            EntriesInWindow = filteredGames.Sum(g => g.RequestCount),
            GameSpeeds = filteredGames,
            ClientSpeeds = filteredClients
        };
    }

    private static bool IsVisibleClient(string clientIp, IReadOnlyCollection<string> hiddenClientIps) =>
        !hiddenClientIps.Contains(clientIp);

    private static GameSpeedInfo CloneGameSpeed(GameSpeedInfo game) => new()
    {
        DepotId = game.DepotId,
        GameName = game.GameName,
        GameAppId = game.GameAppId,
        Service = game.Service,
        ClientIp = game.ClientIp,
        BytesPerSecond = game.BytesPerSecond,
        TotalBytes = game.TotalBytes,
        RequestCount = game.RequestCount,
        CacheHitBytes = game.CacheHitBytes,
        CacheMissBytes = game.CacheMissBytes,
        IsEvicted = game.IsEvicted
    };

    /// <summary>
    /// Publishes the current visible active-download set into the unified activity registry so every
    /// live-download indicator reads one presence signal (the same event the schedule/operation/presence
    /// dots use). A broadcast failure is swallowed so presence can never disturb the authoritative speed
    /// path. Reports both the per-game traffic key and each active client IP so game- and client-scoped
    /// dots can both resolve.
    /// </summary>
    private async Task PublishDownloadActivityAsync(DownloadSpeedSnapshot visible)
    {
        if (_activityRegistry is null)
        {
            return;
        }

        try
        {
            var active = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var game in visible.GameSpeeds)
            {
                active[BuildDownloadActivityKey(game)] = 1;
            }
            foreach (var client in visible.ClientSpeeds)
            {
                var ip = (client.ClientIp ?? string.Empty).Trim();
                if (ip.Length > 0)
                {
                    active[ip] = 1;
                }
            }

            await _activityRegistry.ReplaceAsync(ActivityDomains.Download, ActivityAspects.Downloading, active);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish download activity snapshot");
        }
    }

    // Mirror of the frontend buildTrafficKey (Web/src/components/features/downloads/liveDownloadPreviews.ts):
    // the live-download status dots read activity by this exact client-qualified identity, so this and the
    // TypeScript version must stay in sync. Identity tiers: app id (Steam always keys by app, never by name),
    // then unresolved depot, then a resolved title for named services, then the service-only bucket.
    internal static readonly Regex _steamAppPlaceholder = new(@"^Steam App \d+$", RegexOptions.Compiled);

    internal static readonly Dictionary<string, string> _serviceFallbackLabels =
        new(StringComparer.Ordinal)
        {
            ["epic"] = "Epic Games",
            ["epicgames"] = "Epic Games",
            ["origin"] = "EA / Origin",
            ["ea"] = "EA / Origin",
            ["blizzard"] = "Blizzard / Battle.net",
            ["battlenet"] = "Blizzard / Battle.net",
            ["battle.net"] = "Blizzard / Battle.net",
            ["riot"] = "Riot Games",
            ["riotgames"] = "Riot Games",
            ["xbox"] = "Xbox Live",
            ["xboxlive"] = "Xbox Live",
            ["wsus"] = "Windows Update",
            ["windows"] = "Windows Update",
            ["uplay"] = "Ubisoft",
            ["ubisoft"] = "Ubisoft",
            ["arenanet"] = "ArenaNet",
            ["sony"] = "PlayStation",
            ["playstation"] = "PlayStation",
            ["nintendo"] = "Nintendo",
            ["rockstar"] = "Rockstar Games",
            ["wargaming"] = "Wargaming",
            ["steam"] = "Steam",
            ["localhost"] = "Localhost",
            ["ip-address"] = "Direct IP",
            ["unknown"] = "Unknown Service",
        };

    private static string BuildDownloadActivityKey(GameSpeedInfo game)
    {
        var service = NormalizeServiceName(game.Service);
        var client = (game.ClientIp ?? string.Empty).Trim();
        var appId = PreviewGameAppId(game);
        var depotId = PreviewDepotId(game);

        string identity;
        if (appId is not null)
        {
            identity = $"app:{appId}";
        }
        else if (depotId is not null)
        {
            identity = $"depot:{depotId}";
        }
        else if (IsResolvedGameName(game.GameName, game.Service))
        {
            identity = $"name:{NormalizeTitle(game.GameName)}";
        }
        else
        {
            identity = "service";
        }

        return $"{service}|{client}|{identity}";
    }

    private static long? PreviewGameAppId(GameSpeedInfo game) => game.GameAppId is > 0 ? game.GameAppId : null;

    private static long? PreviewDepotId(GameSpeedInfo game) =>
        PreviewGameAppId(game) is null && game.DepotId > 0 ? game.DepotId : null;

    private static bool IsResolvedGameName(string? gameName, string? service)
    {
        var name = (gameName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return false;
        }

        var normalized = name.ToLowerInvariant();
        var raw = NormalizeServiceName(service);
        if (normalized == raw)
        {
            return false;
        }

        if (_serviceFallbackLabels.TryGetValue(raw, out var fallback) &&
            normalized == fallback.ToLowerInvariant())
        {
            return false;
        }

        return !_steamAppPlaceholder.IsMatch(name);
    }

    private static string NormalizeServiceName(string? service) =>
        (service ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeTitle(string? title) =>
        (title ?? string.Empty).Trim().ToLowerInvariant();

    protected override bool IsEnabled()
    {
        var datasources = _datasourceService.GetDatasources();
        var hasEnabledDatasource = false;
        foreach (var datasource in datasources)
        {
            if (datasource.Enabled)
            {
                hasEnabledDatasource = true;
                break;
            }
        }

        if (!hasEnabledDatasource)
        {
            _logger.LogWarning("No enabled datasources configured, RustSpeedTrackerService will not run");
            return false;
        }

        _rustExecutablePath = _pathResolver.GetRustSpeedTrackerPath();
        if (!File.Exists(_rustExecutablePath))
        {
            _logger.LogWarning("Rust speed tracker not found at {Path}, speed tracking disabled", _rustExecutablePath);
            return false;
        }

        return true;
    }

    /// <summary>
    /// How long to wait before spawning the tracker again after it stopped. Starts at
    /// ErrorRetryDelay and doubles per consecutive failure up to the ceiling, the same
    /// exponential shape LiveLogMonitorService applies to its permission backoff.
    /// </summary>
    private TimeSpan RestartDelay(int consecutiveFailures)
    {
        var seconds = ErrorRetryDelay.TotalSeconds * Math.Pow(2, Math.Max(consecutiveFailures - 1, 0));
        return TimeSpan.FromSeconds(Math.Min(seconds, _maxRestartDelay.TotalSeconds));
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var datasources = _datasourceService.GetDatasources();
        var rustExecutablePath = _rustExecutablePath ?? _pathResolver.GetRustSpeedTrackerPath();
        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Build log directory arguments. The tracker discovers and tails every log source in
                // each directory (the monolithic cachelog access.log AND per-service bare-metal
                // *-access.log files), so any datasource whose scheme supports live speed is passed its
                // directory. Datasources with no single trustworthy layout (Unknown/Mixed) are skipped.
                var logDirs = datasources
                    .Where(d => d.Enabled && _capabilityService.GetCapabilities(d).CanTrackLiveSpeed)
                    .Select(d => $"\"{d.LogPath}\"")
                    .ToList();

                if (logDirs.Count == 0)
                {
                    if (!_loggedNoTrackableDatasources)
                    {
                        _loggedNoTrackableDatasources = true;
                        _logger.LogInformation(
                            "No datasource with trackable log sources; live speed tracking is idle");
                    }
                    // Idle without error spam; re-check periodically in case a source appears.
                    consecutiveFailures = 0;
                    await SafeDelayAsync(TimeSpan.FromSeconds(60), stoppingToken);
                    continue;
                }

                var startedAt = DateTime.UtcNow;

                // Spawning is a transition into "no answer yet", so the window is measured from
                // here rather than from construction. Startup can put minutes between the two:
                // the schedule registry is resolved eagerly before app.Run(), which builds every
                // hosted service, and those constructors read the state file and the database.
                lock (_snapshotLock)
                {
                    _unreportedSinceUtc = startedAt;
                }

                await AnnounceScanBlockedIfChangedAsync();

                // Arming the clock also sets a time at which the answer changes back with nothing
                // to report it, so the one announcement for that moment is booked here.
                _ = AnnounceScanBlockedWhenWindowExpiresAsync(stoppingToken);

                await RunTrackerAsync(rustExecutablePath, logDirs, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // The only other way out of RunTrackerAsync is the child process exiting on its
                // own, and nothing about that throws. Without a wait on this path the loop
                // respawns the tracker as fast as a process can be started, so a child that dies
                // immediately (an unreachable database, for example) burns a core and floods the
                // log instead of backing off.
                consecutiveFailures = DateTime.UtcNow - startedAt >= _healthyRunDuration
                    ? 1
                    : consecutiveFailures + 1;
                var exitRestartDelay = RestartDelay(consecutiveFailures);
                _logger.LogWarning(
                    "Rust speed tracker exited on its own ({Count} in a row), restarting in {Delay}",
                    consecutiveFailures, exitRestartDelay);
                await SafeDelayAsync(exitRestartDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                var errorRestartDelay = RestartDelay(consecutiveFailures);
                _logger.LogError(ex, "Error in RustSpeedTrackerService, restarting in {Delay}", errorRestartDelay);
                await SafeDelayAsync(errorRestartDelay, stoppingToken);
            }
        }
    }

    private async Task RunTrackerAsync(
        string rustExecutablePath,
        IReadOnlyList<string> logDirs,
        CancellationToken stoppingToken)
    {
        var arguments = string.Join(" ", logDirs);

        _logger.LogInformation("Starting Rust speed tracker: {Path} {Args}", rustExecutablePath, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = rustExecutablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(rustExecutablePath)
        };

        // Pass TZ environment variable to Rust
        var tz = Environment.GetEnvironmentVariable("TZ");
        if (!string.IsNullOrEmpty(tz))
        {
            startInfo.EnvironmentVariables["TZ"] = tz;
        }

        _rustProcess = Process.Start(startInfo);

        if (_rustProcess == null)
        {
            throw new Exception("Failed to start Rust speed tracker process");
        }

        _processManager.Track(_rustProcess);

        _logger.LogInformation("Rust speed tracker started with PID {Pid}", _rustProcess.Id);

        // Monitor stderr in background
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _rustProcess.StandardError.ReadLineAsync(stoppingToken)) != null)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogInformation("[speed_tracker stderr] {Line}", line);
                }
            }
        }, stoppingToken);

        // Read stdout for JSON speed snapshots
        try
        {
            while (!stoppingToken.IsCancellationRequested && !_rustProcess.HasExited)
            {
                var line = await _rustProcess.StandardOutput.ReadLineAsync(stoppingToken);

                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                try
                {
                    var snapshot = JsonSerializer.Deserialize<DownloadSpeedSnapshot>(line, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (snapshot != null)
                    {
                        lock (_snapshotLock)
                        {
                            _currentSnapshot = snapshot;
                            _unreportedSinceUtc = null;
                        }

                        // Broadcast the client-visible projection: hidden clients must be filtered
                        // BEFORE the hub send (the REST endpoint uses the same builder), otherwise
                        // a hidden client leaks through SignalR even though it is absent from every
                        // REST response. Speed activity gating uses the same projection, so a
                        // hidden-only download broadcasts no speeds; the scan-blocked signal below
                        // is the one thing that still reports it, deliberately.
                        var visibleSnapshot = BuildClientVisibleSnapshot(
                            snapshot,
                            _stateService.GetHiddenClientIps(),
                            _stateService.GetEvictedDataMode());

                        var hasActivity = visibleSnapshot.HasActiveDownloads;

                        // Broadcast every active snapshot plus exactly one trailing zero so a
                        // real end-of-activity edge is reported once, then stay silent while
                        // idle. The Rust window now adapts to the observed log-delivery
                        // cadence, so a zero reading here means activity genuinely stopped
                        // rather than a gap between flush bursts, and no repeat count is
                        // needed to smooth it for the frontend.
                        if (hasActivity || _previousHadActivity)
                        {
                            await _notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, visibleSnapshot);

                            // Mirror the SAME visible active set into the unified activity registry so every
                            // live-download status dot reads one presence signal. Reported AFTER (and never
                            // gating) the authoritative speed send; on the trailing-zero the empty set clears
                            // every download dot.
                            await PublishDownloadActivityAsync(visibleSnapshot);
                        }

                        if (_previousHadActivity && !hasActivity)
                        {
                            // Downloads just ended - refresh the DB-backed active list once.
                            await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, null);
                        }

                        _previousHadActivity = hasActivity;

                        AnnounceDownloadsEndedIfStopped(snapshot);

                        await AnnounceScanBlockedIfChangedAsync();
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to parse speed snapshot JSON: {Line}", line);
                }
            }

            // Reaching here without a stop request means the tracker process died. Clear the
            // stored snapshot so the restart gap can never keep serving the last active
            // reading, and close out visible activity with one trailing zero broadcast (an
            // application shutdown exits via OperationCanceledException above instead).
            if (!stoppingToken.IsCancellationRequested)
            {
                var emptySnapshot = new DownloadSpeedSnapshot { WindowSeconds = 2 };
                lock (_snapshotLock)
                {
                    _currentSnapshot = emptySnapshot;

                    // Death is the other transition into "no answer yet". Every transition arms
                    // the clock; only a published snapshot clears it. A crash loop therefore
                    // arms repeatedly, but it cannot block scans indefinitely because the restart
                    // delay doubles from five seconds toward five minutes, so the armed share of
                    // each cycle shrinks, and a tracker that never spawns at all arms only once
                    // at construction.
                    _unreportedSinceUtc = DateTime.UtcNow;
                }

                await AnnounceScanBlockedIfChangedAsync();

                // A death arms the clock the same way a spawn does, and the restart delay doubles
                // from five seconds toward five minutes, so most of the gap that follows is time
                // the gate spends allowing scans with nobody told.
                _ = AnnounceScanBlockedWhenWindowExpiresAsync(stoppingToken);

                if (_previousHadActivity)
                {
                    _previousHadActivity = false;
                    var emptyVisible = BuildClientVisibleSnapshot(
                        emptySnapshot,
                        _stateService.GetHiddenClientIps(),
                        _stateService.GetEvictedDataMode());
                    await _notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, emptyVisible);
                    await PublishDownloadActivityAsync(emptyVisible);
                }
            }
        }
        finally
        {
            if (_rustProcess != null)
            {
                if (!_rustProcess.HasExited)
                {
                    _logger.LogInformation("Stopping Rust speed tracker");
                    _processManager.KillProcessTree(_rustProcess, "speed tracker stop");
                    await _processManager.WaitAfterKillAsync(_rustProcess, TimeSpan.FromSeconds(5));
                }

                _processManager.Untrack(_rustProcess);
                _rustProcess.Dispose();
                _rustProcess = null;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_rustProcess != null && !_rustProcess.HasExited)
        {
            _logger.LogInformation("Stopping Rust speed tracker process");
            _processManager.KillProcessTree(_rustProcess, "speed tracker service stop");
            await _processManager.WaitAfterKillAsync(_rustProcess, TimeSpan.FromSeconds(5));
        }

        await base.StopAsync(cancellationToken);
    }
}
