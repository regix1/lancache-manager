using System.Collections;
using System.Diagnostics;
using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace LancacheManager.Tests;

/// <summary>
/// Replays random orders of cancel, force stop, restart, failed outcome saves, log steps, live import
/// passes, manual log batches and queued removals against the real repair lifecycle, log file lock
/// and removal owner. An iteration is two runs: a removal run drives a real game or service removal
/// over two datasources that hold history rows, and a restart run drives a removal record step by
/// step through stops and restarts of the repair owner over one state directory. Every event is
/// followed by a settle, then by the checks that hold at that moment; the checks that need every job
/// to have ended run once the run has released its holds. The numbered invariants: (1) every card
/// ends; (2) a repair is never canceled without a cancel, never lost, and runs or fails out with its
/// error shown; (3) a removal that conflicts with a repair is refused until the repair ends, then
/// starts; (4) history rows are neither lost nor duplicated and leave only with their log step;
/// (5) a stored log position never passes the end of access.log and moves back only for an owed reset
/// or by the lines a purge removed below it.
/// </summary>
public sealed class RepairOrderStressTests(ITestOutputHelper output)
{
    // One iteration (a removal run and a restart run) averaged 1311 to 1447 ms over two 20-iteration
    // runs on Windows under the test lock, so floor(50 s / 1450 ms) = 34 keeps the class near 50 s.
    // Never fewer than 20, so each run family gets at least 20 runs.
    private const int Iterations = 34;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private enum RemovalEvent
    {
        Cancel,
        ForceStop,
        Release,
        LivePass,
        ManualBatch,
        Queue,
        Reopen
    }

    // The first four are the job's own steps, in the order it takes them.
    private enum RestartEvent
    {
        Launch,
        Accept,
        LogStepStarted,
        LogStepKept,
        Finish,
        Cancel,
        ForceStop,
        Restart,
        OwnerFinish,
        ArmSaveFailure,
        LivePass,
        ManualBatch,
        Queue,
        ArmScanFailures
    }

    [Fact]
    public async Task RandomEventOrdersKeepRepairsCardsRowsAndPositionsConsistentAsync()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("LANCACHE_STRESS_SEED"), out var replay)
            ? replay
            : Random.Shared.Next();
        output.WriteLine($"Seed {seed}; replay with LANCACHE_STRESS_SEED={seed}");
        var started = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            // Each run draws from its own generator, so a replayed seed repeats every run's events
            // even where timing changed an earlier run.
            await RunAsync(
                seed,
                iteration,
                "removal",
                new Random(unchecked(seed * 31 + iteration * 2)),
                (random, events) => new RemovalRun(random, events).RunAsync());
            await RunAsync(
                seed,
                iteration,
                "restart",
                new Random(unchecked(seed * 31 + iteration * 2 + 1)),
                (random, events) => new RestartRun(random, events).RunAsync());
        }
        output.WriteLine(
            $"{Iterations} iterations, {Stopwatch.GetElapsedTime(started).TotalMilliseconds / Iterations:F0} ms each");
    }

    private static async Task RunAsync(
        int seed,
        int iteration,
        string run,
        Random random,
        Func<Random, EventLog, Task> body)
    {
        var events = new EventLog();
        try
        {
            await body(random, events);
        }
        catch (Exception exception)
        {
            throw new XunitException(
                $"Seed {seed}, iteration {iteration}, {run} run: {exception.Message}{Environment.NewLine}"
                + $"Events:{Environment.NewLine}{events}{Environment.NewLine}{exception}");
        }
    }

    private static XunitException Invariant(int number, string observed) =>
        new($"Invariant {number} failed: {observed}");

    private static async Task WaitUntilAsync(Func<bool> condition, int invariant, Func<string> observed)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > Bound)
            {
                throw Invariant(invariant, observed());
            }
            await Task.Delay(25);
        }
    }

    // A stopped owner's own work may end with the stop's cancel; every other step ends on its own.
    private static async Task DrainAsync(List<Task> pending, bool stopped)
    {
        foreach (var task in pending)
        {
            try
            {
                await task.WaitAsync(Bound);
            }
            catch (TimeoutException)
            {
                throw Invariant(1, "a background step did not finish within 5 s");
            }
            catch (OperationCanceledException) when (stopped)
            {
            }
        }
        pending.Clear();
    }

    // Invariant 3: while one repair blocks before and after the check, a removal of the same kind is
    // refused with the repairPending conflict.
    private static async Task<OperationConflictResponse?> ProbeConflictAsync(
        OperationStateService owner,
        OperationConflictChecker checker,
        OperationType type)
    {
        var before = owner.GetBlockingRepair();
        var conflict = await checker.CheckAsync(
            type,
            type == OperationType.ServiceRemoval ? ConflictScope.Service("steam") : ConflictScope.SteamGame(570),
            CancellationToken.None);
        var after = owner.GetBlockingRepair();
        var repairPending = conflict?.Context is { } context
            && context.TryGetValue("repairPending", out var flag)
            && flag is true;
        if (before is not null && after?.Id == before.Id && !repairPending)
        {
            throw Invariant(
                3,
                $"repair {before.Id} ({before.Type}, {before.Phase}) blocks, but a new {type} check returned "
                + (conflict is null ? "no conflict" : conflict.StageKey));
        }
        return conflict;
    }

    // Invariant 2 at a settled moment: only a job that got a cancel or force stop ends or stores a
    // cancel, a record that was repairing never leaves the store, and a job that launched native work
    // and ended left a repairing or completed record. The ended jobs are read before the records.
    private static void CheckRecords(
        UnifiedOperationTracker tracker,
        IReadOnlyCollection<Guid> ended,
        IReadOnlyList<OperationRepair> repairs,
        HashSet<Guid> canceled,
        HashSet<Guid> seenRepairing)
    {
        foreach (var operationId in ended)
        {
            if (tracker.GetOperation(operationId) is { Status: OperationStatus.Cancelled }
                && !canceled.Contains(operationId))
            {
                throw Invariant(2, $"card {operationId} ended canceled without a cancel");
            }
        }
        foreach (var repair in repairs)
        {
            if (repair.Outcome == OperationStatus.Cancelled && !canceled.Contains(repair.Id))
            {
                throw Invariant(2, $"repair {repair.Id} stored a cancel its job never got");
            }
            if (repair.Phase == OperationRepairPhase.Repairing)
            {
                seenRepairing.Add(repair.Id);
            }
            if (ended.Contains(repair.Id)
                && repair.Sources.Any(source => source.NativeLaunchAuthorized)
                && repair.Phase is not (OperationRepairPhase.Repairing or OperationRepairPhase.Completed))
            {
                throw Invariant(2, $"job {repair.Id} ended after launching native work, but its record is {repair.Phase}");
            }
        }
        foreach (var operationId in seenRepairing)
        {
            if (repairs.All(repair => repair.Id != operationId))
            {
                throw Invariant(2, $"repair {operationId} left the store after it was repairing");
            }
        }
    }

    // Invariants 1 and 2 once every job ended: no card is left live and every repair completed.
    private static void CheckEnded(UnifiedOperationTracker tracker, IReadOnlyList<OperationRepair> repairs)
    {
        if (tracker.GetActiveOperations().FirstOrDefault() is { } live)
        {
            throw Invariant(1, $"card {live.Id} ({live.Type}) is still {live.Status} after every job ended");
        }
        if (repairs.FirstOrDefault(repair => repair.Phase != OperationRepairPhase.Completed) is { } open)
        {
            throw Invariant(2, $"repair {open.Id} ({open.Type}) is {open.Phase} after every job ended");
        }
    }

    private static string Describe(IReadOnlyList<OperationRepair> repairs) => string.Join(
        "; ",
        repairs.Select(repair => $"{repair.Id} {repair.Type} {repair.Phase} {repair.Outcome}"));

    private static IDictionary Field(OperationStateService owner, string name) =>
        (IDictionary)typeof(OperationStateService)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner)!;

    // Opened with every share mode, so a check never refuses a pass that appends at the same moment.
    private static long CountLines(string accessLog)
    {
        using var reader = new StreamReader(new FileStream(
            accessLog,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
        long lines = 0;
        while (reader.ReadLine() is not null)
        {
            lines++;
        }
        return lines;
    }

    private static RemovalMetrics Metrics(OperationType type) => type == OperationType.ServiceRemoval
        ? new RemovalMetrics { EntityKey = "steam", EntityName = "steam", EntityKind = "service" }
        : new RemovalMetrics { EntityKey = "570", EntityName = "Dota 2", EntityKind = "steam" };

    private sealed class EventLog
    {
        private readonly List<string> _lines = [];

        internal void Add(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }

        public override string ToString()
        {
            lock (_lines)
            {
                return string.Join(Environment.NewLine, _lines.Select((line, index) => $"{index}: {line}"));
            }
        }
    }

    /// <summary>
    /// The positions a simulated import stored for one datasource. An import only moves a position
    /// forward to the end of the file it read; only a repair that redoes an unfinished log step moves
    /// it back to 0, and that reset stays owed until an import runs after the repair ended. A purge
    /// moves it back by the lines it removed below it.
    /// </summary>
    private sealed class ImportedPositions
    {
        private readonly HashSet<long> _stored = [0];
        private long _newest;
        private bool _resetOwed;
        private bool _repairEnded;

        internal void Import(string accessLog, Action<long> store)
        {
            File.AppendAllText(accessLog, "GET /live HTTP/1.1\n");
            var lines = CountLines(accessLog);
            // Known before it is stored, so a check never meets a value it has not seen.
            lock (_stored)
            {
                _stored.Add(lines);
            }
            store(lines);
            lock (_stored)
            {
                _newest = Math.Max(_newest, lines);
                _resetOwed &= !_repairEnded;
            }
        }

        internal void OweReset()
        {
            lock (_stored)
            {
                _resetOwed = true;
                _repairEnded = false;
            }
        }

        internal void RepairEnded()
        {
            lock (_stored)
            {
                _repairEnded = _resetOwed;
            }
        }

        // Runs while the purge holds the logs, so no import stores a value between the purge and the
        // position it brings back.
        internal void Purged(long removedBefore)
        {
            lock (_stored)
            {
                var moved = _stored.Select(value => Math.Max(0, value - removedBefore)).ToList();
                _stored.Clear();
                _stored.UnionWith(moved);
                _newest = Math.Max(0, _newest - removedBefore);
            }
        }

        // Invariant 5.
        internal void Check(string datasource, long stored, long lines)
        {
            lock (_stored)
            {
                if (stored > lines || !_stored.Contains(stored) || !_resetOwed && stored < _newest)
                {
                    throw Invariant(
                        5,
                        $"{datasource} stores position {stored} with {lines} lines in access.log; imports stored "
                        + $"{string.Join(", ", _stored.Order())}, reset owed {_resetOwed}");
                }
            }
        }
    }

    private sealed record RemovalStep(RemovalEvent Kind, bool AfterQuiet, int Pick, string Datasource);

    private sealed record TargetRow(string Datasource, string Url, long? DepotId);

    private sealed record RestartStep(RestartEvent Kind, int Arg);

    /// <summary>
    /// A real game or service removal over alpha and beta with history rows on both. Beta's cache
    /// step may wait for a release and the first purge may run until a cancel, so events land inside
    /// both steps; a queued removal of the same target parks behind the first one and its repair. Each
    /// purge that publishes removes the first line of its access.log.
    /// </summary>
    private sealed class RemovalRun(Random random, EventLog events)
    {
        private const long GameAppId = 570;
        private static readonly string[] Datasources = ["alpha", "beta"];

        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "lm-repair-stress-" + Guid.NewGuid().ToString("N"));
        private readonly List<Guid> _removals = [];
        private readonly HashSet<Guid> _canceled = [];
        private readonly HashSet<Guid> _seenRepairing = [];
        private readonly List<Task> _pending = [];
        private readonly List<TargetRow> _targets = [];
        private readonly Dictionary<string, ImportedPositions> _positions = Datasources.ToDictionary(
            datasource => datasource,
            _ => new ImportedPositions());
        private readonly SemaphoreSlim _queue = new(1, 1);
        private readonly CancellationTokenSource _teardown = new();
        private RemovalRepairHarness _harness = null!;
        private OperationConflictChecker _checker = null!;
        private OperationCancellationService _cancellation = null!;
        private OperationType _type;
        private Guid _first;
        private bool _released;
        private int _otherRows;
        private int _insertsStarted;
        private int _insertsDone;
        private int _passes;

        internal async Task RunAsync()
        {
            _type = random.Next(2) == 0 ? OperationType.GameRemoval : OperationType.ServiceRemoval;
            var betaSucceeds = random.Next(2) == 0;
            var heldPurges = random.Next(2);
            var reportUrl = random.Next(2) == 0;
            var reportDepot = random.Next(2) == 0;
            foreach (var datasource in Datasources)
            {
                var rows = random.Next(3);
                var depot = random.Next(2) == 0 && _type == OperationType.GameRemoval;
                for (var index = 0; index < rows; index++)
                {
                    _targets.Add(new TargetRow(datasource, $"/target/{datasource}/{index}", depot ? 1 : null));
                }
            }
            var plan = new List<RemovalStep>();
            for (var count = random.Next(3, 8); count > 0; count--)
            {
                plan.Add(new RemovalStep(
                    (RemovalEvent)random.Next(7),
                    random.Next(2) == 0,
                    random.Next(1000),
                    Datasources[random.Next(2)]));
            }

            await using var harness = await RemovalRepairHarness.CreateProducerAsync(_root, _type);
            _harness = harness;
            _checker = new OperationConflictChecker(
                harness.Tracker,
                harness.Owner,
                NullLogger<OperationConflictChecker>.Instance);
            _cancellation = new OperationCancellationService(
                harness.Tracker,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                harness.Owner,
                NullLogger<OperationCancellationService>.Instance);
            var rust = harness.Rust;
            rust.BetaSucceeds = betaSucceeds;
            rust.HeldPurges = heldPurges;
            rust.OnLinePurged = (datasource, removedBefore) => _positions[datasource].Purged(removedBefore);
            if (reportUrl)
            {
                rust.ReportUrls.Add("/report/url");
            }
            if (reportDepot && _type == OperationType.GameRemoval)
            {
                rust.ReportDepotIds.Add(1);
            }
            await using (var seed = rust.Contexts.CreateDbContext())
            {
                foreach (var row in _targets)
                {
                    AddRow(seed, row.Datasource, "steam", GameAppId, row.DepotId, row.Url);
                }
                foreach (var datasource in Datasources)
                {
                    AddRow(seed, datasource, "epicgames", gameAppId: null, depotId: null, "/other/" + datasource);
                    _otherRows++;
                }
                await seed.SaveChangesAsync();
            }

            try
            {
                _first = await StartRemovalAsync(RemoveAsync);
                events.Add(
                    $"start {_type} {_first}: beta cache step {(betaSucceeds ? "succeeds" : "waits for a release")}, "
                    + $"first purge {(heldPurges > 0 ? "runs until a cancel" : "ends")}, {_targets.Count} target rows");
                foreach (var step in plan)
                {
                    if (step.AfterQuiet)
                    {
                        await QuietAsync();
                    }
                    await ApplyAsync(step);
                    await SettleAsync();
                    await CheckAsync(final: false);
                }
                await EndAsync();
                await CheckAsync(final: true);
            }
            finally
            {
                // A failed run leaves no step of its own waiting on a release or on a pass.
                await _teardown.CancelAsync();
                harness.ReleaseRust();
            }
        }

        private async Task ApplyAsync(RemovalStep step)
        {
            switch (step.Kind)
            {
                case RemovalEvent.Cancel:
                case RemovalEvent.ForceStop:
                    var live = Removals().Where(operationId => !Ended(operationId)).ToList();
                    if (live.Count == 0)
                    {
                        events.Add($"{step.Kind}: no removal is running");
                        return;
                    }
                    var target = live[step.Pick % live.Count];
                    // The hold gives a cancel a running purge to cut short. A cancel that lands before
                    // the first purge launched could make a repair's purge the first one, and no
                    // cancel reaches a repair.
                    if (!_harness.Rust.Launches.Any(launch => launch.Purge))
                    {
                        _harness.Rust.HeldPurges = 0;
                    }
                    _canceled.Add(target);
                    if (step.Kind == RemovalEvent.Cancel)
                    {
                        events.Add($"cancel {target}: {_cancellation.Cancel(target)}");
                        return;
                    }
                    events.Add($"force stop {target}");
                    await _cancellation.ForceKillAsync(target).WaitAsync(Bound);
                    return;
                case RemovalEvent.Release:
                    _released = true;
                    _harness.ReleaseRust();
                    events.Add("release beta's waiting cache step");
                    return;
                case RemovalEvent.Queue:
                    events.Add("queue another removal of the same target");
                    _pending.Add(QueueRemovalAsync());
                    return;
                case RemovalEvent.Reopen:
                    events.Add("scheduled nginx reopen");
                    _pending.Add(ReopenAsync());
                    return;
                default:
                    var manual = step.Kind == RemovalEvent.ManualBatch;
                    events.Add($"{(manual ? "manual log batch" : "live import pass")} on {step.Datasource}");
                    _pending.Add(PassAsync(step.Datasource, manual));
                    return;
            }
        }

        // Held: the removal waits on a step only a later event ends, beta's cache step until a release
        // or the first purge until a cancel.
        private Task QuietAsync() => WaitUntilAsync(
            () => Ended(_first)
                || !_harness.Rust.BetaSucceeds && !_released && _harness.Rust.BetaProgress.IsCompleted
                || _harness.Rust.HeldPurges > 0 && _harness.Rust.Launches.Any(launch => launch.Purge),
            1,
            () => $"removal {_first} neither ended nor reached a held step");

        private async Task SettleAsync()
        {
            await QuietAsync();
            if (!Ended(_first))
            {
                // Pending steps may wait behind the held step.
                return;
            }
            await DrainAsync(_pending, stopped: false);
            await WaitUntilAsync(
                () => Removals().All(Ended)
                    && Repairs().All(repair => repair.Phase == OperationRepairPhase.Completed),
                2,
                () => $"removals still running: {string.Join(", ", Removals().Where(operationId => !Ended(operationId)))}; "
                    + $"repairs: {Describe(Repairs())}");
        }

        private async Task EndAsync()
        {
            if (!Ended(_first))
            {
                _released = true;
                _harness.ReleaseRust();
                events.Add("end: release beta's waiting cache step");
                await QuietAsync();
            }
            if (!Ended(_first))
            {
                _canceled.Add(_first);
                events.Add($"end: cancel {_first} in its running purge: {_cancellation.Cancel(_first)}");
            }
            await WaitUntilAsync(() => Ended(_first), 1, () => $"removal {_first} did not end");
            await SettleAsync();
        }

        private async Task CheckAsync(bool final)
        {
            var ended = Removals().Where(Ended).ToList();
            var repairs = Repairs();
            CheckRecords(_harness.Tracker, ended, repairs, _canceled, _seenRepairing);
            await ProbeConflictAsync(_harness.Owner, _checker, _type);
            await CheckRowsAsync(repairs, final);
            // Read under the import's lock, as an import reads them, so a purge that shortened
            // access.log has also brought the position back. A step that keeps the logs longer, such
            // as a purge held until a cancel, leaves the positions to a later check.
            using var wait = new CancellationTokenSource(final ? Bound : TimeSpan.FromMilliseconds(200));
            LogFileLock? logs = null;
            try
            {
                logs = await _harness.Owner.LockLogFilesAsync(
                    null,
                    OperationType.LogProcessing,
                    LogFileLockKind.Ingest,
                    wait.Token);
            }
            catch (OperationCanceledException) when (!final && wait.IsCancellationRequested)
            {
            }
            if (logs is not null)
            {
                await using (logs)
                {
                    foreach (var datasource in Datasources)
                    {
                        _positions[datasource].Check(
                            datasource,
                            _harness.Rust.State.GetLogPosition(datasource),
                            CountLines(AccessLog(datasource)));
                    }
                }
            }
            if (_harness.Rust.Launches.Any(launch => launch.Purge && !launch.StepHeld))
            {
                throw Invariant(4, "a purge ran while no log step held the logs");
            }
            if (final)
            {
                CheckEnded(_harness.Tracker, repairs);
            }
        }

        // Invariant 4: rows of other services are neither lost nor duplicated, and at the end each
        // datasource holds every target row, or none once the first removal's log step there kept its
        // positions, each row gone with a purge that named its line.
        private async Task CheckRowsAsync(IReadOnlyList<OperationRepair> repairs, bool final)
        {
            var low = _otherRows + Volatile.Read(ref _insertsDone);
            await using var context = _harness.Rust.Contexts.CreateDbContext();
            var downloads = await context.Downloads.CountAsync(download => download.Service == "epicgames");
            var entries = await context.LogEntries.CountAsync(entry => entry.Service == "epicgames");
            var high = _otherRows + Volatile.Read(ref _insertsStarted);
            if (downloads < low || downloads > high || entries < low || entries > high)
            {
                throw Invariant(
                    4,
                    $"other services hold {downloads} downloads and {entries} log entries; expected {low} to {high}");
            }
            var shared = await context.LogEntries
                .GroupBy(entry => new { entry.Datasource, entry.Url, entry.Timestamp })
                .Where(group => group.Count() > 1)
                .Select(group => group.Key.Url)
                .ToListAsync();
            if (shared.Count > 0)
            {
                throw Invariant(4, $"log entries share datasource, URL and time: {string.Join(", ", shared)}");
            }
            if (!final)
            {
                return;
            }

            var first = repairs.FirstOrDefault(repair => repair.Id == _first);
            var purges = _harness.Rust.Launches
                .Where(launch => launch.Purge)
                .Select(launch => launch.PurgeInput!.Value)
                .ToList();
            foreach (var datasource in Datasources)
            {
                var kept = first?.Sources.Single(source => source.Datasource == datasource).LogPositionsKept == true;
                var seeded = _targets.Where(row => row.Datasource == datasource).ToList();
                var targetDownloads = await context.Downloads.CountAsync(
                    download => download.Datasource == datasource && download.Service == "steam");
                var targetEntries = await context.LogEntries.CountAsync(
                    entry => entry.Datasource == datasource && entry.Service == "steam");
                var expected = kept ? 0 : seeded.Count;
                if (targetDownloads != expected || targetEntries != expected)
                {
                    throw Invariant(
                        4,
                        $"{datasource} holds {targetDownloads} target downloads and {targetEntries} log entries; "
                        + $"expected {expected} (log step kept: {kept}, seeded {seeded.Count})");
                }
                if (!kept)
                {
                    continue;
                }
                foreach (var row in seeded)
                {
                    if (!purges.Any(input =>
                            input.GetProperty("urls").EnumerateArray().Any(url => url.GetString() == row.Url)
                            || row.DepotId is { } depotId
                            && input.GetProperty("depot_ids").EnumerateArray().Any(depot => depot.GetInt64() == depotId)))
                    {
                        throw Invariant(4, $"{datasource} row {row.Url} is gone, but no purge named its line");
                    }
                }
            }
        }

        private async Task<Guid> StartRemovalAsync(
            Func<Guid, CancellationToken, Func<RemovalProgressUpdate, Task>, Task<(int Files, long Bytes)>> execute)
        {
            var operationId = await TrackedRemovalOperationRunner.StartAsync(
                _harness.Tracker,
                _harness.NotificationService,
                _harness.CreateConfig(_type, Metrics(_type), execute));
            lock (_removals)
            {
                _removals.Add(operationId);
            }
            return operationId;
        }

        private async Task<(int Files, long Bytes)> RemoveAsync(
            Guid operationId,
            CancellationToken cancellationToken,
            Func<RemovalProgressUpdate, Task> report)
        {
            Task Progress(double percent, string stage, Dictionary<string, object?>? context, int files, long bytes) =>
                report(new RemovalProgressUpdate(percent, stage, context, files, bytes));
            if (_type == OperationType.GameRemoval)
            {
                var game = await _harness.Manager.RemoveGameFromCacheAsync(
                    GameAppId,
                    cancellationToken,
                    Progress,
                    operationId);
                return (game.CacheFilesDeleted, checked((long)game.TotalBytesFreed));
            }
            var service = await _harness.Manager.RemoveServiceFromCacheAsync(
                "steam",
                cancellationToken,
                Progress,
                operationId);
            return (service.CacheFilesDeleted, checked((long)service.TotalBytesFreed));
        }

        private async Task QueueRemovalAsync()
        {
            await _queue.WaitAsync(_teardown.Token);
            try
            {
                while (await ProbeConflictAsync(_harness.Owner, _checker, _type) is not null)
                {
                    await Task.Delay(25, _teardown.Token);
                }
                // The fake serves one removal's two cache steps, so the queued removal does what the
                // queue orders: it starts its work behind any repair, then ends.
                var queued = await StartRemovalAsync(async (operationId, cancellationToken, _) =>
                {
                    await _harness.Owner.StartWorkAsync(operationId, "alpha", cancellationToken);
                    return (0, 0L);
                });
                events.Add($"queued {_type} {queued} starts");
                while (!Ended(queued))
                {
                    await Task.Delay(25, _teardown.Token);
                }
            }
            finally
            {
                _queue.Release();
            }
        }

        private async Task ReopenAsync()
        {
            await using (await _harness.Owner.LockLogFilesAsync(
                             null,
                             OperationType.LogRotation,
                             LogFileLockKind.Reopen,
                             _teardown.Token))
            {
                // A reopen changes no file, row or position, but never runs inside a log step.
                if (await RemovalRepairHarness.StepHeldAsync(_harness.Owner))
                {
                    throw Invariant(5, "a log step held the logs while a reopen held them");
                }
            }
        }

        private async Task PassAsync(string datasource, bool manual)
        {
            // A live import pass registers as RustLogProcessorService.cs:1092-1099 does, so the log lock gives
            // it the live import's turn beside a reopen; a manual batch registers as a plain run.
            var passId = manual
                ? _harness.Tracker.RegisterOperation(
                    OperationType.LogProcessing,
                    "Manual log batch",
                    new CancellationTokenSource())
                : _harness.Tracker.RegisterOperation(
                    OperationType.LogProcessing,
                    "Log Processing",
                    new CancellationTokenSource(),
                    notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled),
                    liveIngest: true);
            await using (await _harness.Owner.LockLogFilesAsync(
                             passId,
                             OperationType.LogProcessing,
                             LogFileLockKind.Ingest,
                             _teardown.Token))
            {
                var pass = Interlocked.Increment(ref _passes);
                Interlocked.Increment(ref _insertsStarted);
                await using (var context = _harness.Rust.Contexts.CreateDbContext())
                {
                    AddRow(context, datasource, "epicgames", gameAppId: null, depotId: null, $"/live/{datasource}/{pass}");
                    await context.SaveChangesAsync();
                }
                Interlocked.Increment(ref _insertsDone);
                // Stored per source as the import stores it, the map a purge's report brings back.
                _positions[datasource].Import(
                    AccessLog(datasource),
                    position => _harness.Rust.State.SetLogSourcePositions(
                        datasource,
                        new Dictionary<string, long> { ["access.log"] = position }));
            }
            _harness.Tracker.CompleteOperation(passId, success: true);
        }

        private List<Guid> Removals()
        {
            lock (_removals)
            {
                return [.. _removals];
            }
        }

        private IReadOnlyList<OperationRepair> Repairs() => _harness.Rust.State.LoadOperationRepairs();

        private bool Ended(Guid operationId) =>
            _harness.Tracker.GetOperation(operationId) is not { } operation || operation.Status.IsTerminal();

        private string AccessLog(string datasource) => Path.Combine(_root, datasource + "-logs", "access.log");

        private static void AddRow(
            AppDbContext context,
            string datasource,
            string service,
            long? gameAppId,
            long? depotId,
            string url)
        {
            var now = DateTime.UtcNow;
            var download = new Download
            {
                Service = service,
                ClientIp = "10.0.0.5",
                Datasource = datasource,
                GameAppId = gameAppId,
                DepotId = depotId,
                StartTimeUtc = now.AddMinutes(-1),
                EndTimeUtc = now
            };
            context.LogEntries.Add(new LogEntryRecord
            {
                Download = download,
                Service = service,
                Datasource = datasource,
                ClientIp = "10.0.0.5",
                Url = url,
                Timestamp = now,
                CreatedAt = now
            });
        }
    }

    /// <summary>
    /// A removal record that the test drives step by step through the repair owner, the way a job
    /// does, while a restart replaces the owner with a new one over the same state directory and
    /// access.log. A failed outcome save and failed cache scans are retried at once here, because the
    /// harness owns the repair clock.
    /// </summary>
    private sealed class RestartRun(Random random, EventLog events)
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "lm-repair-stress-" + Guid.NewGuid().ToString("N"));
        private readonly List<Guid> _cards = [];
        private readonly HashSet<Guid> _canceled = [];
        private readonly HashSet<Guid> _seenRepairing = [];
        private readonly List<Task> _pending = [];
        private readonly ImportedPositions _positions = new();
        private readonly SemaphoreSlim _queue = new(1, 1);
        private readonly CancellationTokenSource _jobCancel = new();
        private CancellationTokenSource _teardown = new();
        private OperationRepairTests.DispatchHarness _harness = null!;
        private OperationRepairTests.DispatchHarness? _stopped;
        private OperationConflictChecker _checker = null!;
        private OperationCancellationService _cancellation = null!;
        private OperationType _type;
        private Guid _job;
        private LogFileLock? _jobLock;
        private int _reach;
        private bool _jobAlive;
        private bool _logStepOpen;
        private bool _ownerFinishOwed;

        private string AccessLog => Path.Combine(_root, "alpha-logs", "access.log");

        internal async Task RunAsync()
        {
            _type = random.Next(2) == 0 ? OperationType.GameRemoval : OperationType.ServiceRemoval;
            var plan = Plan();
            Use(await OperationRepairTests.DispatchHarness.CreateAsync(_root));
            try
            {
                var repair = _harness.NewRemoval(_type);
                repair.Id = _job = _harness.Repairs.Tracker.RegisterOperation(
                    _type,
                    "Stress removal",
                    _jobCancel,
                    Metrics(_type),
                    ownerCompletes: true);
                AddCard(_job);
                await _harness.Owner.PrepareRepairAsync(repair, CancellationToken.None);
                _jobAlive = true;
                events.Add($"start {_type} {_job}; it takes {_reach} of its 4 steps");
                foreach (var step in plan)
                {
                    await ApplyAsync(step);
                    await SettleAsync();
                    await CheckAsync(final: false);
                }
                await EndAsync();
                await CheckAsync(final: true);
            }
            finally
            {
                await _teardown.CancelAsync();
                // A restart that failed after stopping the old harness has no new one to stop.
                if (_harness != _stopped)
                {
                    await _harness.DisposeAsync();
                }
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
        }

        private List<RestartStep> Plan()
        {
            RestartEvent[] sides =
                [RestartEvent.LivePass, RestartEvent.ManualBatch, RestartEvent.Queue, RestartEvent.ArmScanFailures];
            RestartEvent[] endings =
                [RestartEvent.Finish, RestartEvent.Cancel, RestartEvent.ForceStop, RestartEvent.Restart];
            var plan = new List<RestartStep>();
            void AddSides(int most)
            {
                for (var count = random.Next(most + 1); count > 0; count--)
                {
                    plan.Add(new RestartStep(sides[random.Next(sides.Length)], random.Next(1, 4)));
                }
            }

            _reach = random.Next(5);
            for (var step = 0; step < _reach; step++)
            {
                AddSides(1);
                plan.Add(new RestartStep((RestartEvent)step, random.Next(2)));
            }
            AddSides(1);
            var ending = endings[random.Next(endings.Length)];
            // Armed only where the ending's own outcome save writes a repairing record, so that save
            // is the one that fails.
            if (random.Next(3) == 0 && _reach > 0 && ending != RestartEvent.Restart)
            {
                plan.Add(new RestartStep(RestartEvent.ArmSaveFailure, 0));
            }
            plan.Add(new RestartStep(ending, 0));
            if (ending == RestartEvent.ForceStop)
            {
                AddSides(1);
                // Otherwise the owner reports at the end, or never when a restart comes first.
                if (random.Next(3) != 0)
                {
                    plan.Add(new RestartStep(RestartEvent.OwnerFinish, 0));
                }
            }
            AddSides(2);
            if (random.Next(2) == 0)
            {
                plan.Add(new RestartStep(RestartEvent.Restart, 0));
                AddSides(1);
            }
            return plan;
        }

        private async Task ApplyAsync(RestartStep step)
        {
            var owner = _harness.Owner;
            switch (step.Kind)
            {
                case RestartEvent.Launch:
                    events.Add($"{_job} starts its cache step");
                    await owner.StartWorkAsync(_job, "alpha", _jobCancel.Token).WaitAsync(Bound);
                    return;
                case RestartEvent.Accept:
                    var depot = step.Arg == 1 && _type == OperationType.GameRemoval;
                    events.Add($"{_job} cache step is accepted{(depot ? " with depot 1" : string.Empty)}");
                    await owner.SaveRepairAsync(
                        _job,
                        repair =>
                        {
                            repair.Sources.Single().NativeCompletionAccepted = true;
                            if (depot)
                            {
                                repair.Target!.SteamDepotIds = [1];
                            }
                        },
                        CancellationToken.None);
                    return;
                case RestartEvent.LogStepStarted:
                    events.Add($"{_job} log step takes the logs and starts");
                    _jobLock = await owner.LockLogFilesAsync(_job, _type, LogFileLockKind.Rewrite, _jobCancel.Token)
                        .WaitAsync(Bound);
                    await owner.MarkLogRewriteStartedAsync(_job, "alpha");
                    _logStepOpen = true;
                    return;
                case RestartEvent.LogStepKept:
                    events.Add($"{_job} log step keeps its positions and lets go of the logs");
                    await owner.MarkLogPositionsKeptAsync(_job, "alpha");
                    _logStepOpen = false;
                    await ReleaseJobLockAsync();
                    return;
                case RestartEvent.Finish:
                    var success = _reach == 4;
                    events.Add($"{_job} ends {(success ? "completed" : "failed")}");
                    await EndJobAsync(success ? OperationStatus.Completed : OperationStatus.Failed);
                    return;
                case RestartEvent.Cancel:
                    _canceled.Add(_job);
                    events.Add($"cancel {_job}: {_cancellation.Cancel(_job)}");
                    await EndJobAsync(OperationStatus.Cancelled);
                    return;
                case RestartEvent.ForceStop:
                    _canceled.Add(_job);
                    events.Add($"force stop {_job}");
                    // The stopped job unwinds its step; its own report comes later.
                    await StopJobAsync();
                    _ownerFinishOwed = true;
                    await _cancellation.ForceKillAsync(_job).WaitAsync(Bound);
                    return;
                case RestartEvent.OwnerFinish:
                    if (!_ownerFinishOwed)
                    {
                        events.Add($"{_job} never reports: its process restarted");
                        return;
                    }
                    events.Add($"{_job} reports its cancel after the force stop");
                    _ownerFinishOwed = false;
                    await owner.FinishRepairAsync(_job, false, true, null).WaitAsync(Bound);
                    return;
                case RestartEvent.Restart:
                    await RestartAsync();
                    return;
                case RestartEvent.ArmSaveFailure:
                    events.Add("the next save of a repairing record fails");
                    _harness.State.FailRepairStarts = 1;
                    return;
                case RestartEvent.ArmScanFailures:
                    events.Add($"the next {step.Arg} cache scans fail");
                    _harness.FailScans = step.Arg;
                    return;
                case RestartEvent.Queue:
                    events.Add("queue another removal of the same target");
                    _pending.Add(QueueRemovalAsync());
                    return;
                default:
                    var manual = step.Kind == RestartEvent.ManualBatch;
                    events.Add(manual ? "manual log batch" : "live import pass");
                    _pending.Add(PassAsync(manual));
                    return;
            }
        }

        private async Task EndJobAsync(OperationStatus outcome)
        {
            await StopJobAsync();
            var error = outcome == OperationStatus.Failed ? "Injected job failure." : null;
            await _harness.Owner.FinishRepairAsync(
                    _job,
                    outcome == OperationStatus.Completed,
                    outcome == OperationStatus.Cancelled,
                    error)
                .WaitAsync(Bound);
            _harness.Repairs.Tracker.CompleteOperation(
                _job,
                outcome == OperationStatus.Completed,
                error,
                cancelled: outcome == OperationStatus.Cancelled);
        }

        // The job stops working: a log step it left open is owed a reset by its repair, and its lock goes.
        private async Task StopJobAsync()
        {
            if (_logStepOpen)
            {
                _positions.OweReset();
                _logStepOpen = false;
            }
            _jobAlive = false;
            await ReleaseJobLockAsync();
        }

        private async Task ReleaseJobLockAsync()
        {
            if (_jobLock is { } held)
            {
                _jobLock = null;
                await held.DisposeAsync();
            }
        }

        private async Task RestartAsync()
        {
            events.Add(_jobAlive ? $"restart while {_job} runs" : "restart");
            await StopJobAsync();
            _ownerFinishOwed = false;
            await _teardown.CancelAsync();
            var stopped = _stopped = _harness;
            await stopped.DisposeAsync();
            await WaitUntilAsync(
                () => Field(stopped.Owner, "_repairTasks").Count == 0,
                2,
                () => "the stopped owner still runs a repair");
            await DrainAsync(_pending, stopped: true);
            // The new harness writes its own first line; a restart keeps the file the old process left.
            var accessLog = await File.ReadAllTextAsync(AccessLog);
            var next = await OperationRepairTests.DispatchHarness.CreateAsync(_root, start: false);
            await File.WriteAllTextAsync(AccessLog, accessLog);
            _teardown = new CancellationTokenSource();
            Use(next);
            await next.Owner.StartAsync(CancellationToken.None);
        }

        private async Task EndAsync()
        {
            if (_ownerFinishOwed)
            {
                await ApplyAsync(new RestartStep(RestartEvent.OwnerFinish, 0));
            }
            await DrainAsync(_pending, stopped: false);
            // A repair that failed out, with its card still open, runs again on Retry. At most three
            // armed scan failures remain, so a few rounds always end with every repair completed.
            for (var round = 0; round < 4; round++)
            {
                await SettleAsync();
                var failedOut = _harness.State.LoadOperationRepairs()
                    .Where(repair => repair.Phase == OperationRepairPhase.Repairing && FailedOut(repair.Id))
                    .Select(repair => repair.Id)
                    .ToList();
                if (failedOut.Count == 0)
                {
                    break;
                }
                foreach (var operationId in failedOut)
                {
                    events.Add($"retry {operationId}");
                    if (!await _harness.Owner.RetryRepairAsync(operationId))
                    {
                        throw Invariant(2, $"repair {operationId} failed out, but Retry refused to run it");
                    }
                }
            }
            await WaitUntilAsync(
                () => _harness.State.LoadOperationRepairs()
                    .All(repair => repair.Phase == OperationRepairPhase.Completed),
                2,
                () => $"repairs did not complete: {Describe(_harness.State.LoadOperationRepairs())}");
        }

        // Settled: every repairing record runs its repair and waits only for its force-stopped owner or
        // for running work, or has failed out with the error on its card; the stopped job's record
        // holds its outcome.
        private Task SettleAsync() => WaitUntilAsync(
            () =>
            {
                var tasks = Field(_harness.Owner, "_repairTasks");
                var stoppedOwners = Field(_harness.Owner, "_forceStoppedOwners");
                var repairs = _harness.State.LoadOperationRepairs();
                var working = repairs.Any(repair => repair.Phase == OperationRepairPhase.Running);
                return repairs.All(repair => repair.Phase switch
                {
                    OperationRepairPhase.Prepared or OperationRepairPhase.Running => repair.Id != _job || _jobAlive,
                    OperationRepairPhase.Repairing => FailedOut(repair.Id)
                        || tasks.Contains(repair.Id) && (stoppedOwners.Contains(repair.Id) || working),
                    _ => true
                });
            },
            2,
            () => $"repairs did not settle: {Describe(_harness.State.LoadOperationRepairs())}");

        private bool FailedOut(Guid operationId)
        {
            var failures = Field(_harness.Owner, "_repairFailures");
            return failures.Contains(operationId)
                && (int)failures[operationId]! >= 3
                && !Field(_harness.Owner, "_repairTasks").Contains(operationId)
                && _harness.Repairs.Tracker.GetOperation(operationId)?.RepairError is not null;
        }

        private async Task CheckAsync(bool final)
        {
            var tracker = _harness.Repairs.Tracker;
            List<Guid> ended;
            lock (_cards)
            {
                ended = _cards
                    .Where(operationId => operationId == _job
                        ? !_jobAlive
                        : tracker.GetOperation(operationId) is not { } card || card.Status.IsTerminal())
                    .ToList();
            }
            var repairs = _harness.State.LoadOperationRepairs();
            CheckRecords(tracker, ended, repairs, _canceled, _seenRepairing);
            if (repairs.Any(repair => repair.Id == _job && repair.Phase == OperationRepairPhase.Completed))
            {
                _positions.RepairEnded();
            }
            await ProbeConflictAsync(_harness.Owner, _checker, _type);
            _positions.Check("alpha", _harness.State.GetLogPosition("alpha"), CountLines(AccessLog));
            if (final)
            {
                CheckEnded(tracker, repairs);
            }
        }

        private async Task QueueRemovalAsync()
        {
            var harness = _harness;
            var checker = _checker;
            var teardown = _teardown.Token;
            await _queue.WaitAsync(teardown);
            try
            {
                while (await ProbeConflictAsync(harness.Owner, checker, _type) is not null)
                {
                    await Task.Delay(25, teardown);
                }
                var repair = harness.NewRemoval(_type);
                repair.Id = harness.Repairs.Tracker.RegisterOperation(
                    _type,
                    "Queued removal",
                    new CancellationTokenSource(),
                    Metrics(_type),
                    ownerCompletes: true);
                AddCard(repair.Id);
                events.Add($"queued {_type} {repair.Id} starts");
                await harness.Owner.PrepareRepairAsync(repair, teardown);
                await harness.Owner.StartWorkAsync(repair.Id, "alpha", teardown);
                await harness.Owner.FinishRepairAsync(repair.Id, true, false, null);
                harness.Repairs.Tracker.CompleteOperation(repair.Id, success: true);
            }
            finally
            {
                _queue.Release();
            }
        }

        private async Task PassAsync(bool manual)
        {
            var harness = _harness;
            var tracker = harness.Repairs.Tracker;
            // A live import pass registers as RustLogProcessorService.cs:1092-1099 does, so the log lock gives
            // it the live import's turn beside a reopen; a manual batch registers as a plain run.
            var passId = manual
                ? tracker.RegisterOperation(OperationType.LogProcessing, "Manual log batch", new CancellationTokenSource())
                : tracker.RegisterOperation(
                    OperationType.LogProcessing,
                    "Log Processing",
                    new CancellationTokenSource(),
                    notice: new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled),
                    liveIngest: true);
            await using (await harness.Owner.LockLogFilesAsync(
                             passId,
                             OperationType.LogProcessing,
                             LogFileLockKind.Ingest,
                             _teardown.Token))
            {
                _positions.Import(AccessLog, position => harness.State.SetLogPosition("alpha", position));
            }
            tracker.CompleteOperation(passId, success: true);
        }

        private void Use(OperationRepairTests.DispatchHarness harness)
        {
            _harness = harness;
            _checker = new OperationConflictChecker(
                harness.Repairs.Tracker,
                harness.Owner,
                NullLogger<OperationConflictChecker>.Instance);
            _cancellation = new OperationCancellationService(
                harness.Repairs.Tracker,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                harness.Owner,
                NullLogger<OperationCancellationService>.Instance);
        }

        private void AddCard(Guid operationId)
        {
            lock (_cards)
            {
                _cards.Add(operationId);
            }
        }
    }
}
