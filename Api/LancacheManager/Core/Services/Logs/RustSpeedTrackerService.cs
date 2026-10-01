using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
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
    private DownloadSpeedSnapshot _currentSnapshot;
    private readonly object _snapshotLock = new();
    private readonly TimeProvider _clock;
    private readonly string _streamId;
    private readonly SemaphoreSlim _ageWake = new(0, 1);
    private readonly Channel<DownloadSpeedSnapshot> _publication;
    private readonly Dictionary<Guid, Dictionary<string, string>> _runSources = new();
    private readonly Dictionary<string, Guid> _sourceRuns = new(StringComparer.Ordinal);
    private Guid _currentRunId;
    private long _nativeRevision;
    private long _revision;
    private long _edgeRevision;
    private long _queuedRevision;
    private DateTime _agingUtc;
    private string _visibilityMark = string.Empty;
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
        IActivityRegistry? activityRegistry = null,
        TimeProvider? clock = null)
        : base(logger, configuration)
    {
        _pathResolver = pathResolver;
        _datasourceService = datasourceService;
        _notifications = notifications;
        _processManager = processManager;
        _capabilityService = capabilityService;
        _stateService = stateService;
        _activityRegistry = activityRegistry;
        _clock = clock ?? TimeProvider.System;
        _streamId = Guid.NewGuid().ToString("N");
        _agingUtc = UtcNow();
        _unreportedSinceUtc = _agingUtc;
        _currentSnapshot = EmptySnapshot(isAvailable: false, _agingUtc);
        _publication = Channel.CreateBounded<DownloadSpeedSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _activityRegistry?.BindDownloads(GetCurrentSnapshot);
    }

    /// <summary>
    /// Gets the current CLIENT-VISIBLE speed snapshot: hidden clients are filtered out and the
    /// evicted-data display mode is applied, exactly as the SignalR broadcast does, so REST and
    /// SignalR always expose identical visibility semantics.
    /// </summary>
    public DownloadSpeedSnapshot GetCurrentSnapshot()
    {
        var hiddenClientIps = _stateService.GetHiddenClientIps();
        var evictedMode = _stateService.GetEvictedDataMode();
        DownloadSpeedSnapshot raw;
        bool changed;
        lock (_snapshotLock)
        {
            changed = AgeLocked(UtcNow(), CurrentSources());
            var visibilityMark = string.Join('\u001f', hiddenClientIps.OrderBy(ip => ip, StringComparer.Ordinal)) +
                "\u001e" + evictedMode;
            if (_visibilityMark.Length == 0)
            {
                _visibilityMark = visibilityMark;
            }
            else if (!string.Equals(_visibilityMark, visibilityMark, StringComparison.Ordinal))
            {
                _visibilityMark = visibilityMark;
                _revision++;
                RebuildLocked(CurrentEntriesLocked(), _agingUtc, _currentSnapshot.IsAvailable);
                changed = true;
            }
            raw = CloneSnapshot(_currentSnapshot);
        }

        if (changed)
        {
            PublishReadChange(
                raw,
                BuildClientVisibleSnapshot(raw, hiddenClientIps, evictedMode));
        }

        return BuildClientVisibleSnapshot(raw, hiddenClientIps, evictedMode);
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
        bool changed;
        DownloadSpeedSnapshot snapshot;
        DateTime? unreportedSinceUtc;
        lock (_snapshotLock)
        {
            changed = AgeLocked(UtcNow(), CurrentSources());
            snapshot = CloneSnapshot(_currentSnapshot);
            unreportedSinceUtc = _unreportedSinceUtc;
        }

        if (changed)
        {
            PublishReadChange(
                snapshot,
                BuildClientVisibleSnapshot(
                    snapshot,
                    _stateService.GetHiddenClientIps(),
                    _stateService.GetEvictedDataMode()));
        }

        return (unreportedSinceUtc, snapshot);
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

        var filteredClients = filteredGames
            .Where(g => !string.IsNullOrWhiteSpace(g.ClientIp))
            .GroupBy(g => g.ClientIp, StringComparer.Ordinal)
            .Select(group => new ClientSpeedInfo
            {
                ClientIp = group.Key,
                BytesPerSecond = group.Sum(g => g.BytesPerSecond),
                TotalBytes = group.Sum(g => g.TotalBytes),
                ActiveGames = group.Count(),
                CacheHitBytes = group.Sum(g => g.CacheHitBytes),
                CacheMissBytes = group.Sum(g => g.CacheMissBytes),
                ActiveUntilUtc = group.Max(g => g.ActiveUntilUtc),
            })
            .OrderBy(c => c.ClientIp, StringComparer.Ordinal)
            .ToList();

        return new DownloadSpeedSnapshot
        {
            Version = snapshot.Version,
            StreamId = snapshot.StreamId,
            Revision = snapshot.Revision,
            TimestampUtc = snapshot.TimestampUtc,
            IsAvailable = snapshot.IsAvailable,
            WindowSeconds = snapshot.WindowSeconds,
            TotalBytesPerSecond = filteredGames.Sum(g => g.BytesPerSecond),
            EntriesInWindow = filteredGames.Sum(g => g.RequestCount),
            GameSpeeds = filteredGames,
            ClientSpeeds = filteredClients,
        };
    }

    private static bool IsVisibleClient(string clientIp, IReadOnlyCollection<string> hiddenClientIps) =>
        !hiddenClientIps.Contains(clientIp);

    private static GameSpeedInfo CloneGameSpeed(GameSpeedInfo game) => new()
    {
        Key = game.Key,
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
        IsEvicted = game.IsEvicted,
        FirstSeenUtc = game.FirstSeenUtc,
        LastSeenUtc = game.LastSeenUtc,
        ActiveUntilUtc = game.ActiveUntilUtc,
        Sources = game.Sources.Select(CloneSource).ToList(),
    };

    private static DownloadSource CloneSource(DownloadSource source) => new()
    {
        Datasources = [.. source.Datasources],
        DepotIds = [.. source.DepotIds],
        FirstSeenUtc = source.FirstSeenUtc,
        LastSeenUtc = source.LastSeenUtc,
        ActiveUntilUtc = source.ActiveUntilUtc,
        MeasuredUntilUtc = source.MeasuredUntilUtc,
        BytesPerSecond = source.BytesPerSecond,
        TotalBytes = source.TotalBytes,
        RequestCount = source.RequestCount,
        CacheHitBytes = source.CacheHitBytes,
        CacheMissBytes = source.CacheMissBytes,
    };

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

    internal static string BuildDownloadActivityKey(GameSpeedInfo game)
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

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private DownloadSpeedSnapshot EmptySnapshot(bool isAvailable, DateTime timestampUtc) => new()
    {
        Version = 2,
        StreamId = _streamId,
        Revision = _revision,
        TimestampUtc = timestampUtc,
        IsAvailable = isAvailable,
        WindowSeconds = 2,
    };

    private static DownloadSpeedSnapshot CloneSnapshot(DownloadSpeedSnapshot snapshot) => new()
    {
        Version = snapshot.Version,
        StreamId = snapshot.StreamId,
        Revision = snapshot.Revision,
        TimestampUtc = snapshot.TimestampUtc,
        IsAvailable = snapshot.IsAvailable,
        TotalBytesPerSecond = snapshot.TotalBytesPerSecond,
        GameSpeeds = snapshot.GameSpeeds.Select(CloneGameSpeed).ToList(),
        ClientSpeeds = snapshot.ClientSpeeds.Select(client => new ClientSpeedInfo
        {
            ClientIp = client.ClientIp,
            BytesPerSecond = client.BytesPerSecond,
            TotalBytes = client.TotalBytes,
            ActiveGames = client.ActiveGames,
            CacheHitBytes = client.CacheHitBytes,
            CacheMissBytes = client.CacheMissBytes,
            ActiveUntilUtc = client.ActiveUntilUtc,
        }).ToList(),
        WindowSeconds = snapshot.WindowSeconds,
        EntriesInWindow = snapshot.EntriesInWindow,
    };

    private static bool SameContents(DownloadSpeedSnapshot left, DownloadSpeedSnapshot right)
    {
        if (left.Version != right.Version || left.IsAvailable != right.IsAvailable ||
            left.WindowSeconds != right.WindowSeconds ||
            left.TotalBytesPerSecond != right.TotalBytesPerSecond ||
            left.EntriesInWindow != right.EntriesInWindow ||
            left.GameSpeeds.Count != right.GameSpeeds.Count ||
            left.ClientSpeeds.Count != right.ClientSpeeds.Count)
        {
            return false;
        }

        for (var gameIndex = 0; gameIndex < left.GameSpeeds.Count; gameIndex++)
        {
            var leftGame = left.GameSpeeds[gameIndex];
            var rightGame = right.GameSpeeds[gameIndex];
            if (!string.Equals(leftGame.Key, rightGame.Key, StringComparison.Ordinal) ||
                leftGame.DepotId != rightGame.DepotId ||
                !string.Equals(leftGame.GameName, rightGame.GameName, StringComparison.Ordinal) ||
                leftGame.GameAppId != rightGame.GameAppId ||
                !string.Equals(leftGame.Service, rightGame.Service, StringComparison.Ordinal) ||
                !string.Equals(leftGame.ClientIp, rightGame.ClientIp, StringComparison.Ordinal) ||
                leftGame.BytesPerSecond != rightGame.BytesPerSecond ||
                leftGame.TotalBytes != rightGame.TotalBytes ||
                leftGame.RequestCount != rightGame.RequestCount ||
                leftGame.CacheHitBytes != rightGame.CacheHitBytes ||
                leftGame.CacheMissBytes != rightGame.CacheMissBytes ||
                leftGame.IsEvicted != rightGame.IsEvicted ||
                leftGame.FirstSeenUtc != rightGame.FirstSeenUtc ||
                leftGame.LastSeenUtc != rightGame.LastSeenUtc ||
                leftGame.ActiveUntilUtc != rightGame.ActiveUntilUtc ||
                leftGame.Sources.Count != rightGame.Sources.Count)
            {
                return false;
            }

            for (var sourceIndex = 0; sourceIndex < leftGame.Sources.Count; sourceIndex++)
            {
                var leftSource = leftGame.Sources[sourceIndex];
                var rightSource = rightGame.Sources[sourceIndex];
                if (!leftSource.Datasources.SequenceEqual(
                        rightSource.Datasources,
                        StringComparer.Ordinal) ||
                    !leftSource.DepotIds.SequenceEqual(rightSource.DepotIds) ||
                    leftSource.FirstSeenUtc != rightSource.FirstSeenUtc ||
                    leftSource.LastSeenUtc != rightSource.LastSeenUtc ||
                    leftSource.ActiveUntilUtc != rightSource.ActiveUntilUtc ||
                    leftSource.MeasuredUntilUtc != rightSource.MeasuredUntilUtc ||
                    leftSource.BytesPerSecond != rightSource.BytesPerSecond ||
                    leftSource.TotalBytes != rightSource.TotalBytes ||
                    leftSource.RequestCount != rightSource.RequestCount ||
                    leftSource.CacheHitBytes != rightSource.CacheHitBytes ||
                    leftSource.CacheMissBytes != rightSource.CacheMissBytes)
                {
                    return false;
                }
            }
        }

        for (var clientIndex = 0; clientIndex < left.ClientSpeeds.Count; clientIndex++)
        {
            var leftClient = left.ClientSpeeds[clientIndex];
            var rightClient = right.ClientSpeeds[clientIndex];
            if (!string.Equals(leftClient.ClientIp, rightClient.ClientIp, StringComparison.Ordinal) ||
                leftClient.BytesPerSecond != rightClient.BytesPerSecond ||
                leftClient.TotalBytes != rightClient.TotalBytes ||
                leftClient.ActiveGames != rightClient.ActiveGames ||
                leftClient.CacheHitBytes != rightClient.CacheHitBytes ||
                leftClient.CacheMissBytes != rightClient.CacheMissBytes ||
                leftClient.ActiveUntilUtc != rightClient.ActiveUntilUtc)
            {
                return false;
            }
        }

        return true;
    }

    private Dictionary<string, string> CurrentSources()
    {
        return _datasourceService.GetDatasources()
            .Where(source => source.Enabled && _capabilityService.GetCapabilities(source).CanTrackLiveSpeed)
            .ToDictionary(source => source.Name, source => source.LogPath, StringComparer.OrdinalIgnoreCase);
    }

    private static string SourceKey(string gameKey, DownloadSource source) =>
        $"{gameKey}\u001f{string.Join('\u001e', source.Datasources.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))}";

    private bool AgeLocked(DateTime nowUtc, Dictionary<string, string> currentSources)
    {
        if (nowUtc > _agingUtc)
        {
            _agingUtc = nowUtc;
        }

        nowUtc = _agingUtc;
        var changed = false;
        var retained = new List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>();
        foreach (var game in _currentSnapshot.GameSpeeds)
        {
            foreach (var original in game.Sources)
            {
                var source = CloneSource(original);
                var sourceKey = SourceKey(game.Key, original);
                _sourceRuns.TryGetValue(sourceKey, out var runId);

                if (runId != Guid.Empty && _runSources.TryGetValue(runId, out var captured))
                {
                    var aliases = source.Datasources
                        .Where(name =>
                            captured.TryGetValue(name, out var capturedRoot) &&
                            currentSources.TryGetValue(name, out var currentRoot) &&
                            string.Equals(capturedRoot, currentRoot, StringComparison.Ordinal))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (aliases.Count != source.Datasources.Count)
                    {
                        source.Datasources = aliases;
                        changed = true;
                    }
                }

                if (source.Datasources.Count == 0 || nowUtc >= source.ActiveUntilUtc)
                {
                    changed = true;
                    continue;
                }

                if (nowUtc >= source.MeasuredUntilUtc &&
                    (source.BytesPerSecond != 0 || source.TotalBytes != 0 || source.RequestCount != 0 ||
                     source.CacheHitBytes != 0 || source.CacheMissBytes != 0))
                {
                    source.BytesPerSecond = 0;
                    source.TotalBytes = 0;
                    source.RequestCount = 0;
                    source.CacheHitBytes = 0;
                    source.CacheMissBytes = 0;
                    changed = true;
                }

                retained.Add((game, source, runId));
            }
        }

        if (!changed)
        {
            return false;
        }

        _revision++;
        RebuildLocked(retained, nowUtc, _currentSnapshot.IsAvailable);
        return true;
    }

    private void RebuildLocked(
        IReadOnlyList<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)> entries,
        DateTime timestampUtc,
        bool isAvailable)
    {
        _sourceRuns.Clear();
        var games = entries
            .GroupBy(entry => entry.Game.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var basis = group
                    .OrderByDescending(entry => entry.Source.LastSeenUtc)
                    .First().Game;
                var sources = group
                    .Select(entry => CloneSource(entry.Source))
                    .OrderBy(source => source.Datasources.FirstOrDefault(), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(source => source.FirstSeenUtc)
                    .ToList();
                foreach (var entry in group)
                {
                    _sourceRuns[SourceKey(group.Key, entry.Source)] = entry.RunId;
                }

                return new GameSpeedInfo
                {
                    Key = group.Key,
                    DepotId = basis.DepotId,
                    GameName = basis.GameName,
                    GameAppId = basis.GameAppId,
                    Service = basis.Service,
                    ClientIp = basis.ClientIp,
                    BytesPerSecond = sources.Sum(source => source.BytesPerSecond),
                    TotalBytes = sources.Sum(source => source.TotalBytes),
                    RequestCount = sources.Sum(source => source.RequestCount),
                    CacheHitBytes = sources.Sum(source => source.CacheHitBytes),
                    CacheMissBytes = sources.Sum(source => source.CacheMissBytes),
                    IsEvicted = basis.IsEvicted,
                    FirstSeenUtc = sources.Min(source => source.FirstSeenUtc),
                    LastSeenUtc = sources.Max(source => source.LastSeenUtc),
                    ActiveUntilUtc = sources.Max(source => source.ActiveUntilUtc),
                    Sources = sources,
                };
            })
            .OrderBy(game => game.Key, StringComparer.Ordinal)
            .ToList();

        var clients = games
            .Where(game => !string.IsNullOrWhiteSpace(game.ClientIp))
            .GroupBy(game => game.ClientIp, StringComparer.Ordinal)
            .Select(group => new ClientSpeedInfo
            {
                ClientIp = group.Key,
                BytesPerSecond = group.Sum(game => game.BytesPerSecond),
                TotalBytes = group.Sum(game => game.TotalBytes),
                ActiveGames = group.Count(),
                CacheHitBytes = group.Sum(game => game.CacheHitBytes),
                CacheMissBytes = group.Sum(game => game.CacheMissBytes),
                ActiveUntilUtc = group.Max(game => game.ActiveUntilUtc),
            })
            .OrderBy(client => client.ClientIp, StringComparer.Ordinal)
            .ToList();

        _currentSnapshot = new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = _streamId,
            Revision = _revision,
            TimestampUtc = timestampUtc,
            IsAvailable = isAvailable,
            WindowSeconds = 2,
            TotalBytesPerSecond = games.Sum(game => game.BytesPerSecond),
            EntriesInWindow = games.Sum(game => game.RequestCount),
            GameSpeeds = games,
            ClientSpeeds = clients,
        };

        var retainedRuns = entries.Select(entry => entry.RunId).Where(id => id != Guid.Empty).ToHashSet();
        foreach (var oldRun in _runSources.Keys
                     .Where(id => id != _currentRunId && !retainedRuns.Contains(id))
                     .ToList())
        {
            _runSources.Remove(oldRun);
        }
    }

    internal void QueueSnapshot(DownloadSpeedSnapshot snapshot)
    {
        var queued = CloneSnapshot(snapshot);
        lock (_snapshotLock)
        {
            if (queued.Revision <= _queuedRevision)
            {
                return;
            }

            if (_publication.Writer.TryWrite(queued))
            {
                _queuedRevision = queued.Revision;
                if (_ageWake.CurrentCount == 0)
                {
                    _ageWake.Release();
                }
            }
        }
    }

    private List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)> CurrentEntriesLocked()
    {
        var entries = new List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>();
        foreach (var game in _currentSnapshot.GameSpeeds)
        {
            foreach (var source in game.Sources)
            {
                _sourceRuns.TryGetValue(SourceKey(game.Key, source), out var runId);
                entries.Add((game, CloneSource(source), runId));
            }
        }

        return entries;
    }

    private List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>? PrepareNativeSnapshot(
        DownloadSpeedSnapshot snapshot,
        Guid runId,
        Dictionary<string, string> captured,
        Dictionary<string, string> currentSources,
        DateTime nowUtc)
    {
        if (snapshot.Version != 2 || snapshot.StreamId is null || snapshot.StreamId.Length != 0 || snapshot.Revision <= 0 ||
            snapshot.WindowSeconds != 2 || !snapshot.IsAvailable || snapshot.TimestampUtc == default ||
            snapshot.TimestampUtc.Kind != DateTimeKind.Utc || snapshot.TimestampUtc > nowUtc ||
            snapshot.GameSpeeds is null || snapshot.ClientSpeeds is null ||
            !double.IsFinite(snapshot.TotalBytesPerSecond) || snapshot.TotalBytesPerSecond < 0 ||
            snapshot.EntriesInWindow < 0)
        {
            return null;
        }

        var incoming = new List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>();
        foreach (var game in snapshot.GameSpeeds)
        {
            if (string.IsNullOrWhiteSpace(game.Key) || string.IsNullOrWhiteSpace(game.Service) ||
                game.Sources is null || game.Sources.Count == 0 ||
                game.FirstSeenUtc == default || game.LastSeenUtc == default || game.ActiveUntilUtc == default ||
                game.FirstSeenUtc.Kind != DateTimeKind.Utc || game.LastSeenUtc.Kind != DateTimeKind.Utc ||
                game.ActiveUntilUtc.Kind != DateTimeKind.Utc ||
                game.FirstSeenUtc > game.LastSeenUtc || game.LastSeenUtc >= game.ActiveUntilUtc ||
                !double.IsFinite(game.BytesPerSecond) || game.BytesPerSecond < 0 ||
                game.TotalBytes < 0 || game.RequestCount < 0 ||
                game.CacheHitBytes < 0 || game.CacheMissBytes < 0)
            {
                return null;
            }

            var preparedGame = CloneGameSpeed(game);
            preparedGame.Service = preparedGame.Service.Trim();
            preparedGame.ClientIp = preparedGame.ClientIp.Trim();
            preparedGame.Key = BuildDownloadActivityKey(preparedGame);
            if (string.IsNullOrWhiteSpace(preparedGame.Key))
            {
                return null;
            }

            foreach (var original in game.Sources)
            {
                if (original.Datasources is null || original.Datasources.Count == 0 ||
                    original.Datasources.Any(string.IsNullOrWhiteSpace) || original.DepotIds is null ||
                    original.DepotIds.Any(id => id <= 0) ||
                    original.FirstSeenUtc == default || original.LastSeenUtc == default ||
                    original.ActiveUntilUtc == default || original.MeasuredUntilUtc == default ||
                    original.FirstSeenUtc.Kind != DateTimeKind.Utc ||
                    original.LastSeenUtc.Kind != DateTimeKind.Utc ||
                    original.ActiveUntilUtc.Kind != DateTimeKind.Utc ||
                    original.MeasuredUntilUtc.Kind != DateTimeKind.Utc ||
                    original.FirstSeenUtc > original.LastSeenUtc ||
                    original.LastSeenUtc >= original.ActiveUntilUtc ||
                    original.LastSeenUtc > original.MeasuredUntilUtc ||
                    original.LastSeenUtc > snapshot.TimestampUtc ||
                    !double.IsFinite(original.BytesPerSecond) || original.BytesPerSecond < 0 ||
                    original.TotalBytes < 0 || original.RequestCount < 0 ||
                    original.CacheHitBytes < 0 || original.CacheMissBytes < 0)
                {
                    return null;
                }

                var aliases = original.Datasources
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(name =>
                        captured.TryGetValue(name, out var capturedRoot) &&
                        currentSources.TryGetValue(name, out var currentRoot) &&
                        string.Equals(capturedRoot, currentRoot, StringComparison.Ordinal))
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (aliases.Count == 0)
                {
                    continue;
                }

                if (original.ActiveUntilUtc <= nowUtc)
                {
                    continue;
                }

                var source = CloneSource(original);
                source.Datasources = aliases;
                source.DepotIds = source.DepotIds.Distinct().OrderBy(id => id).ToList();
                if (nowUtc >= source.MeasuredUntilUtc)
                {
                    source.BytesPerSecond = 0;
                    source.TotalBytes = 0;
                    source.RequestCount = 0;
                    source.CacheHitBytes = 0;
                    source.CacheMissBytes = 0;
                }
                incoming.Add((preparedGame, source, runId));
            }
        }

        foreach (var client in snapshot.ClientSpeeds)
        {
            if (string.IsNullOrWhiteSpace(client.ClientIp) || client.ActiveUntilUtc == default ||
                client.ActiveUntilUtc.Kind != DateTimeKind.Utc ||
                !double.IsFinite(client.BytesPerSecond) || client.BytesPerSecond < 0 ||
                client.TotalBytes < 0 || client.ActiveGames < 0 ||
                client.CacheHitBytes < 0 || client.CacheMissBytes < 0)
            {
                return null;
            }
        }

        return incoming;
    }

    private static bool SameMappedSource(
        (GameSpeedInfo Game, DownloadSource Source, Guid RunId) left,
        (GameSpeedInfo Game, DownloadSource Source, Guid RunId) right)
    {
        if (!string.Equals(left.Game.ClientIp, right.Game.ClientIp, StringComparison.Ordinal) ||
            !string.Equals(
                NormalizeServiceName(left.Game.Service),
                NormalizeServiceName(right.Game.Service),
                StringComparison.Ordinal) ||
            !left.Source.Datasources.Intersect(right.Source.Datasources, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        return left.Source.DepotIds.Intersect(right.Source.DepotIds).Any();
    }

    internal async Task AcceptNativeSnapshotAsync(
        DownloadSpeedSnapshot snapshot,
        Guid runId,
        CancellationToken stoppingToken)
    {
        var currentSources = CurrentSources();
        DownloadSpeedSnapshot? raw = null;
        lock (_snapshotLock)
        {
            if (runId != _currentRunId || !_runSources.TryGetValue(runId, out var captured) ||
                snapshot.Revision <= _nativeRevision)
            {
                return;
            }

            var nowUtc = UtcNow();
            if (nowUtc > _agingUtc)
            {
                _agingUtc = nowUtc;
            }

            var incoming = PrepareNativeSnapshot(snapshot, runId, captured, currentSources, _agingUtc);
            if (incoming is null)
            {
                _logger.LogWarning(
                    "Rejected invalid version {Version} speed snapshot revision {Revision}",
                    snapshot.Version,
                    snapshot.Revision);
                return;
            }

            var previousSnapshot = CloneSnapshot(_currentSnapshot);
            var previous = CurrentEntriesLocked();
            var merged = new List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>();
            foreach (var next in incoming)
            {
                var exactKey = SourceKey(next.Game.Key, next.Source);
                var matches = previous
                    .Where(existing =>
                        SourceKey(existing.Game.Key, existing.Source) == exactKey || SameMappedSource(existing, next))
                    .ToList();
                if (matches.Count == 0)
                {
                    merged.Add(next);
                    continue;
                }

                previous.RemoveAll(matches.Contains);
                var existing = matches.OrderByDescending(match => match.Source.LastSeenUtc).First();
                var source = CloneSource(next.Source);
                if (next.Source.LastSeenUtc < existing.Source.LastSeenUtc)
                {
                    merged.AddRange(matches);
                    continue;
                }
                else if (next.Source.LastSeenUtc == existing.Source.LastSeenUtc)
                {
                    var firstSeenUtc = matches.Min(match => match.Source.FirstSeenUtc);
                    source.FirstSeenUtc = firstSeenUtc < source.FirstSeenUtc
                        ? firstSeenUtc
                        : source.FirstSeenUtc;
                    source.ActiveUntilUtc = matches.Max(match => match.Source.ActiveUntilUtc);
                    source.MeasuredUntilUtc = matches.Max(match => match.Source.MeasuredUntilUtc);
                }
                else
                {
                    var firstSeenUtc = matches.Min(match => match.Source.FirstSeenUtc);
                    var activeUntilUtc = matches.Max(match => match.Source.ActiveUntilUtc);
                    source.FirstSeenUtc = firstSeenUtc < source.FirstSeenUtc
                        ? firstSeenUtc
                        : source.FirstSeenUtc;
                    source.ActiveUntilUtc = activeUntilUtc > source.ActiveUntilUtc
                        ? activeUntilUtc
                        : source.ActiveUntilUtc;
                }

                merged.Add((next.Game, source, runId));
            }

            merged.AddRange(previous.Where(existing => existing.RunId != runId));
            _nativeRevision = snapshot.Revision;
            _unreportedSinceUtc = null;
            RebuildLocked(merged, snapshot.TimestampUtc, isAvailable: true);
            if (SameContents(previousSnapshot, _currentSnapshot))
            {
                _currentSnapshot.Revision = previousSnapshot.Revision;
                _currentSnapshot.TimestampUtc = previousSnapshot.TimestampUtc;
            }
            else
            {
                _revision++;
                _currentSnapshot.Revision = _revision;
                raw = CloneSnapshot(_currentSnapshot);
            }
        }

        if (raw is null)
        {
            return;
        }

        var visible = BuildClientVisibleSnapshot(
            raw, _stateService.GetHiddenClientIps(), _stateService.GetEvictedDataMode());
        await PublishChangeAsync(raw, visible, reportingHealthy: true, stoppingToken);
    }

    private bool ApplyChange(
        DownloadSpeedSnapshot raw,
        DownloadSpeedSnapshot visible,
        bool reportingHealthy,
        out bool visibleEnded)
    {
        var downloadsEnded = false;
        visibleEnded = false;
        lock (_snapshotLock)
        {
            if (raw.Revision <= _edgeRevision)
            {
                return false;
            }

            _edgeRevision = raw.Revision;
            if (reportingHealthy)
            {
                downloadsEnded = _previousHadUnfilteredActivity && !raw.HasActiveDownloads;
                visibleEnded = _previousHadActivity && !visible.HasActiveDownloads;
                _previousHadUnfilteredActivity = raw.HasActiveDownloads;
                _previousHadActivity = visible.HasActiveDownloads;
            }
        }

        QueueSnapshot(visible);
        if (downloadsEnded)
        {
            DownloadsEnded?.Invoke();
        }

        return true;
    }

    private async Task NotifyChangeAsync(bool visibleEnded)
    {
        if (visibleEnded)
        {
            try
            {
                await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, null);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to publish the current download end");
            }
        }

        try
        {
            await AnnounceScanBlockedIfChangedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to publish the cache scan admission change");
        }
    }

    private void PublishReadChange(DownloadSpeedSnapshot raw, DownloadSpeedSnapshot visible)
    {
        if (ApplyChange(raw, visible, raw.IsAvailable, out var visibleEnded))
        {
            _ = NotifyChangeAsync(visibleEnded);
        }
    }

    private async Task PublishChangeAsync(
        DownloadSpeedSnapshot raw,
        DownloadSpeedSnapshot visible,
        bool reportingHealthy,
        CancellationToken stoppingToken)
    {
        if (ApplyChange(raw, visible, reportingHealthy, out var visibleEnded))
        {
            await NotifyChangeAsync(visibleEnded);
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    private async Task BeginRunAsync(
        Guid runId,
        IReadOnlyDictionary<string, string> sources,
        DateTime startedAtUtc,
        CancellationToken stoppingToken)
    {
        DownloadSpeedSnapshot? changed = null;
        lock (_snapshotLock)
        {
            _currentRunId = runId;
            _nativeRevision = 0;
            _runSources[runId] = new Dictionary<string, string>(sources, StringComparer.OrdinalIgnoreCase);
            _unreportedSinceUtc = startedAtUtc;
            if (_currentSnapshot.IsAvailable)
            {
                _revision++;
                RebuildLocked(CurrentEntriesLocked(), startedAtUtc, isAvailable: false);
                changed = CloneSnapshot(_currentSnapshot);
            }
        }

        if (changed is not null)
        {
            var visible = BuildClientVisibleSnapshot(
                changed, _stateService.GetHiddenClientIps(), _stateService.GetEvictedDataMode());
            await PublishChangeAsync(changed, visible, reportingHealthy: false, stoppingToken);
        }

        await AnnounceScanBlockedIfChangedAsync();
        _ = AnnounceScanBlockedWhenWindowExpiresAsync(stoppingToken);
    }

    private async Task EndRunAsync(Guid runId, CancellationToken stoppingToken)
    {
        DownloadSpeedSnapshot? changed = null;
        lock (_snapshotLock)
        {
            if (runId != _currentRunId)
            {
                return;
            }

            _unreportedSinceUtc = UtcNow();
            if (_currentSnapshot.IsAvailable)
            {
                _revision++;
                RebuildLocked(CurrentEntriesLocked(), _unreportedSinceUtc.Value, isAvailable: false);
                changed = CloneSnapshot(_currentSnapshot);
            }
        }

        if (changed is not null)
        {
            var visible = BuildClientVisibleSnapshot(
                changed, _stateService.GetHiddenClientIps(), _stateService.GetEvictedDataMode());
            await PublishChangeAsync(changed, visible, reportingHealthy: false, stoppingToken);
        }

        await AnnounceScanBlockedIfChangedAsync();
        _ = AnnounceScanBlockedWhenWindowExpiresAsync(stoppingToken);
    }

    private async Task AgeSnapshotsAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime? boundary;
            lock (_snapshotLock)
            {
                boundary = _currentSnapshot.GameSpeeds
                    .SelectMany(game => game.Sources)
                    .SelectMany(source =>
                    {
                        if (source.BytesPerSecond == 0 && source.TotalBytes == 0 && source.RequestCount == 0 &&
                            source.CacheHitBytes == 0 && source.CacheMissBytes == 0)
                        {
                            return new[] { source.ActiveUntilUtc };
                        }

                        return new[] { source.MeasuredUntilUtc, source.ActiveUntilUtc };
                    })
                    .Where(value => value != default)
                    .Cast<DateTime?>()
                    .Min();
            }

            if (boundary is null)
            {
                await _ageWake.WaitAsync(stoppingToken);
            }
            else
            {
                var delay = boundary.Value - UtcNow();
                if (delay > TimeSpan.Zero)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var delayTask = Task.Delay(delay, _clock, wait.Token);
                    var wakeTask = _ageWake.WaitAsync(wait.Token);
                    await Task.WhenAny(delayTask, wakeTask);
                    await wait.CancelAsync();
                }
            }

            await AgeCurrentSnapshotAsync(stoppingToken);
        }
    }

    internal async Task AgeCurrentSnapshotAsync(CancellationToken stoppingToken = default)
    {
        DownloadSpeedSnapshot? changed = null;
        lock (_snapshotLock)
        {
            if (AgeLocked(UtcNow(), CurrentSources()))
            {
                changed = CloneSnapshot(_currentSnapshot);
            }
        }

        if (changed is not null)
        {
            var visible = BuildClientVisibleSnapshot(
                changed, _stateService.GetHiddenClientIps(), _stateService.GetEvictedDataMode());
            await PublishChangeAsync(changed, visible, changed.IsAvailable, stoppingToken);
        }
    }

    private async Task PublishSnapshotsAsync(CancellationToken stoppingToken)
    {
        long sentRevision = -1;
        await foreach (var snapshot in _publication.Reader.ReadAllAsync(stoppingToken))
        {
            if (snapshot.Revision <= sentRevision)
            {
                continue;
            }

            sentRevision = snapshot.Revision;
            try
            {
                await _notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, snapshot);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to publish current download snapshot");
            }

            if (_activityRegistry is not null)
            {
                try
                {
                    await _activityRegistry.ReplaceDownloadsAsync(snapshot);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to publish current download activity");
                }
            }
        }
    }

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
        var rustExecutablePath = _rustExecutablePath ?? _pathResolver.GetRustSpeedTrackerPath();
        var consecutiveFailures = 0;
        var agingTask = AgeSnapshotsAsync(stoppingToken);
        var publicationTask = PublishSnapshotsAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var sources = _datasourceService.GetDatasources()
                        .Where(source => source.Enabled && _capabilityService.GetCapabilities(source).CanTrackLiveSpeed)
                        .Select(source => (source.Name, Root: source.LogPath))
                        .ToList();

                    if (sources.Count == 0)
                    {
                        if (!_loggedNoTrackableDatasources)
                        {
                            _loggedNoTrackableDatasources = true;
                            _logger.LogInformation(
                                "No datasource with trackable log sources; live speed tracking is idle");
                        }
                        consecutiveFailures = 0;
                        await SafeDelayAsync(TimeSpan.FromSeconds(60), stoppingToken);
                        continue;
                    }

                    _loggedNoTrackableDatasources = false;
                    var startedAt = UtcNow();
                    var runId = Guid.NewGuid();
                    var captured = sources.ToDictionary(
                        source => source.Name,
                        source => source.Root,
                        StringComparer.OrdinalIgnoreCase);
                    await BeginRunAsync(runId, captured, startedAt, stoppingToken);
                    await RunTrackerAsync(rustExecutablePath, sources, runId, stoppingToken);

                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    consecutiveFailures = UtcNow() - startedAt >= _healthyRunDuration
                        ? 1
                        : consecutiveFailures + 1;
                    var exitRestartDelay = RestartDelay(consecutiveFailures);
                    _logger.LogWarning(
                        "Rust speed tracker exited on its own ({Count} in a row), restarting in {Delay}",
                        consecutiveFailures,
                        exitRestartDelay);
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
        finally
        {
            lock (_snapshotLock)
            {
                _publication.Writer.TryComplete();
                if (_ageWake.CurrentCount == 0)
                {
                    _ageWake.Release();
                }
            }

            try
            {
                await Task.WhenAll(agingTask, publicationTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunTrackerAsync(
        string rustExecutablePath,
        List<(string Name, string Root)> sources,
        Guid runId,
        CancellationToken stoppingToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = rustExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(rustExecutablePath)
        };
        foreach (var source in sources)
        {
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(source.Name);
            startInfo.ArgumentList.Add(source.Root);
        }

        _logger.LogInformation("Starting Rust speed tracker from {Path} for {Count} datasource aliases", rustExecutablePath, sources.Count);

        // Pass TZ environment variable to Rust
        var tz = Environment.GetEnvironmentVariable("TZ");
        if (!string.IsNullOrEmpty(tz))
        {
            startInfo.EnvironmentVariables["TZ"] = tz;
        }

        var process = Process.Start(startInfo);
        _rustProcess = process;

        if (process == null)
        {
            throw new Exception("Failed to start Rust speed tracker process");
        }

        _processManager.Track(process);

        _logger.LogInformation("Rust speed tracker started with PID {Pid}", process.Id);

        // Monitor stderr in background
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(stoppingToken)) != null)
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
            while (!stoppingToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync(stoppingToken);

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
                        await AcceptNativeSnapshotAsync(snapshot, runId, stoppingToken);
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogDebug(ex, "Failed to parse speed snapshot JSON: {Line}", line);
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await EndRunAsync(runId, stoppingToken);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                _logger.LogInformation("Stopping Rust speed tracker");
                _processManager.KillProcessTree(process, "speed tracker stop");
                await _processManager.WaitAfterKillAsync(process, TimeSpan.FromSeconds(5));
            }

            _processManager.Untrack(process);
            process.Dispose();
            if (ReferenceEquals(_rustProcess, process))
            {
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
