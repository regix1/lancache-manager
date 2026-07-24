using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Constants;
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
    // clients and prefill traffic can never leak through either transport.
    // Initial value before the first Rust snapshot arrives. Two seconds is the minimum/default
    // window; the Rust tracker reports a window that adapts upward from there toward the
    // observed log-delivery cadence.
    private DownloadSpeedSnapshot _currentSnapshot = new() { WindowSeconds = 2 };
    private readonly object _snapshotLock = new();
    private bool _previousHadActivity = false;

    protected override string ServiceName => "RustSpeedTrackerService";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(5);
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
    /// Gets the current CLIENT-VISIBLE speed snapshot: hidden clients and prefill traffic are
    /// filtered out and the evicted-data display mode is applied, exactly as the SignalR
    /// broadcast does, so REST and SignalR always expose identical visibility semantics.
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
    /// Builds the client-visible snapshot from the raw tracker snapshot. Hidden clients and
    /// prefill traffic (the same exclusions the dashboard applies to recorded downloads) are
    /// removed, the evicted-data display mode is applied, and the top-level totals are
    /// recomputed from the retained entries. Retained game entries are copied so display
    /// rewrites (ShowClean) can never mutate the tracker's raw snapshot.
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
        !hiddenClientIps.Contains(clientIp) &&
        !string.Equals(clientIp, DownloadKindConstants.PrefillToken, StringComparison.OrdinalIgnoreCase);

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
    private static readonly Regex _steamAppPlaceholder = new(@"^Steam App \d+$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> _serviceFallbackLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
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

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        var datasources = _datasourceService.GetDatasources();
        var rustExecutablePath = _rustExecutablePath ?? _pathResolver.GetRustSpeedTrackerPath();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTrackerAsync(rustExecutablePath, datasources, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RustSpeedTrackerService, restarting in 5 seconds");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task RunTrackerAsync(
        string rustExecutablePath,
        IReadOnlyList<ResolvedDatasource> datasources,
        CancellationToken stoppingToken)
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
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            return;
        }

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
                        }

                        // Broadcast the client-visible projection: hidden clients and prefill
                        // traffic must be filtered BEFORE the hub send (the REST endpoint uses
                        // the same builder), otherwise a hidden client leaks through SignalR
                        // even though it is absent from every REST response. Activity gating
                        // uses the same projection so hidden-only traffic broadcasts nothing.
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
                }

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
