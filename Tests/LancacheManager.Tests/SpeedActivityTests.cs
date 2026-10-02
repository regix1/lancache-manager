using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class SpeedActivityTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc);
    private static readonly string FixtureRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(SpeedActivityTests),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MeasurementAndActivityBoundariesChangeOneServerSnapshotAsync()
    {
        var measuredUntilUtc = Now.AddSeconds(2);
        var activeUntilUtc = Now.AddSeconds(15);
        var clock = new MutableClock(new DateTimeOffset(Now));
        var tracker = CacheScanGateHarness.TrackerWith(
            Snapshot(measuredUntilUtc, activeUntilUtc, isAvailable: true),
            [],
            clock);
        var gate = CacheScanGateHarness.GateOver(tracker);

        clock.UtcNow = new DateTimeOffset(measuredUntilUtc.AddTicks(-1));
        var measured = tracker.GetCurrentSnapshot();
        Assert.Equal(500, Assert.Single(measured.GameSpeeds).BytesPerSecond);
        Assert.NotNull(gate.CheckDownloadInProgress());

        clock.UtcNow = new DateTimeOffset(measuredUntilUtc);
        await tracker.AgeCurrentSnapshotAsync();
        var retained = tracker.GetCurrentSnapshot();
        Assert.True(retained.HasActiveDownloads);
        Assert.Equal(0, Assert.Single(retained.GameSpeeds).BytesPerSecond);
        Assert.Equal(0, Assert.Single(retained.GameSpeeds).RequestCount);
        Assert.Equal(activeUntilUtc, Assert.Single(retained.ClientSpeeds).ActiveUntilUtc);
        Assert.NotNull(gate.CheckDownloadInProgress());
        var zeroRevision = retained.Revision;

        clock.UtcNow = new DateTimeOffset(Now);
        await tracker.AgeCurrentSnapshotAsync();
        Assert.Equal(zeroRevision, tracker.GetCurrentSnapshot().Revision);
        Assert.True(tracker.GetCurrentSnapshot().HasActiveDownloads);

        clock.UtcNow = new DateTimeOffset(activeUntilUtc);
        await tracker.AgeCurrentSnapshotAsync();
        var ended = tracker.GetCurrentSnapshot();
        Assert.False(ended.HasActiveDownloads);
        Assert.Empty(ended.GameSpeeds);
        Assert.Empty(ended.ClientSpeeds);
        Assert.True(ended.Revision > zeroRevision);
        Assert.Null(gate.CheckDownloadInProgress());
    }

    [Fact]
    public async Task HiddenZeroRateSessionStillBlocksScansUntilItsBoundaryAsync()
    {
        var measuredUntilUtc = Now.AddSeconds(2);
        var activeUntilUtc = Now.AddSeconds(15);
        var clock = new MutableClock(new DateTimeOffset(measuredUntilUtc));
        var tracker = CacheScanGateHarness.TrackerWith(
            Snapshot(measuredUntilUtc, activeUntilUtc, isAvailable: true),
            ["10.0.0.5"],
            clock);
        await tracker.AgeCurrentSnapshotAsync();

        Assert.Empty(tracker.GetCurrentSnapshot().GameSpeeds);
        Assert.NotNull(CacheScanGateHarness.GateOver(tracker).CheckDownloadInProgress());

        clock.UtcNow = new DateTimeOffset(activeUntilUtc);
        await tracker.AgeCurrentSnapshotAsync();
        Assert.Null(CacheScanGateHarness.GateOver(tracker).CheckDownloadInProgress());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("repointed")]
    [InlineData("untrackable")]
    public async Task CurrentConfigurationFiltersAcceptedAndRetainedSourcesAsync(string change)
    {
        var clock = new MutableClock(new DateTimeOffset(Now));
        var datasources = CurrentDatasources("none", invalidateC: false);
        var tracker = CacheScanGateHarness.TrackerWith(
            new DownloadSpeedSnapshot
            {
                StreamId = "public-stream",
                TimestampUtc = Now,
            },
            [],
            clock,
            datasources);
        var runId = Guid.NewGuid();
        CacheScanGateHarness.SetField(tracker, "_currentRunId", runId);
        CacheScanGateHarness.SetField(
            tracker,
            "_runSources",
            new Dictionary<Guid, Dictionary<string, string>>
            {
                [runId] = CacheScanGateHarness.CaptureRoots(datasources),
            });

        var frame = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        frame.StreamId = string.Empty;
        frame.Revision = 1;
        var game = Assert.Single(frame.GameSpeeds);
        var sourceA = Assert.Single(game.Sources);
        sourceA.Datasources = ["A"];
        var sourceC = new DownloadSource
        {
            Datasources = ["C"],
            DepotIds = [100],
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
            MeasuredUntilUtc = Now.AddSeconds(2),
            ActiveUntilUtc = Now.AddSeconds(15),
            BytesPerSecond = 300,
            TotalBytes = 600,
            RequestCount = 3,
            CacheHitBytes = 450,
            CacheMissBytes = 150,
        };
        game.Sources.Add(sourceC);
        game.BytesPerSecond = 800;
        game.TotalBytes = 1_600;
        game.RequestCount = 5;
        game.CacheHitBytes = 1_200;
        game.CacheMissBytes = 400;
        frame.TotalBytesPerSecond = 800;
        frame.EntriesInWindow = 5;
        var client = Assert.Single(frame.ClientSpeeds);
        client.BytesPerSecond = 800;
        client.TotalBytes = 1_600;
        client.CacheHitBytes = 1_200;
        client.CacheMissBytes = 400;

        await tracker.AcceptNativeSnapshotAsync(frame, runId, CancellationToken.None);

        var accepted = tracker.GetCurrentSnapshot();
        var acceptedGame = Assert.Single(accepted.GameSpeeds);
        Assert.Equal(new[] { "A", "C" }, acceptedGame.Sources.SelectMany(source => source.Datasources));
        Assert.Equal(800, acceptedGame.BytesPerSecond);
        Assert.Equal(1_600, acceptedGame.TotalBytes);
        Assert.Equal(5, acceptedGame.RequestCount);
        Assert.Equal(800, Assert.Single(accepted.ClientSpeeds).BytesPerSecond);
        Assert.Equal(800, accepted.TotalBytesPerSecond);

        // The source map is cached for a minute; dropping it stands in for the next rebuild.
        var changed = CurrentDatasources(change, invalidateC: false);
        CacheScanGateHarness.SetField(tracker, "_datasourceService", changed);
        CacheScanGateHarness.SetField(tracker, "_capabilityService", new DatasourceCapabilityService(changed));
        CacheScanGateHarness.SetField(tracker, "_currentSources", null);

        var retained = tracker.GetCurrentSnapshot();
        var rawRetained = tracker.ReadUnfilteredState().Snapshot;
        var retainedGame = Assert.Single(retained.GameSpeeds);
        var retainedSource = Assert.Single(retainedGame.Sources);
        Assert.Equal(new[] { "C" }, retainedSource.Datasources);
        Assert.Equal(300, retainedGame.BytesPerSecond);
        Assert.Equal(600, retainedGame.TotalBytes);
        Assert.Equal(3, retainedGame.RequestCount);
        Assert.Equal(Now.AddSeconds(15), retainedSource.ActiveUntilUtc);
        Assert.Equal(300, Assert.Single(retained.ClientSpeeds).BytesPerSecond);
        Assert.Equal(300, retained.TotalBytesPerSecond);
        Assert.Equal(retained.Revision, rawRetained.Revision);
        Assert.Equal(new[] { "C" }, Assert.Single(Assert.Single(rawRetained.GameSpeeds).Sources).Datasources);
        Assert.NotNull(CacheScanGateHarness.GateOver(tracker).CheckDownloadInProgress());
        var retainedRevision = retained.Revision;

        frame.Revision = 2;
        await tracker.AcceptNativeSnapshotAsync(frame, runId, CancellationToken.None);
        var acceptedAgain = tracker.GetCurrentSnapshot();
        var rawAcceptedAgain = tracker.ReadUnfilteredState().Snapshot;
        Assert.Equal(retainedRevision, acceptedAgain.Revision);
        Assert.Equal(retainedRevision, rawAcceptedAgain.Revision);
        Assert.Equal(new[] { "C" }, Assert.Single(Assert.Single(acceptedAgain.GameSpeeds).Sources).Datasources);
        Assert.Equal(new[] { "C" }, Assert.Single(Assert.Single(rawAcceptedAgain.GameSpeeds).Sources).Datasources);

        var allInvalid = CurrentDatasources(change, invalidateC: true);
        CacheScanGateHarness.SetField(tracker, "_datasourceService", allInvalid);
        CacheScanGateHarness.SetField(tracker, "_capabilityService", new DatasourceCapabilityService(allInvalid));
        CacheScanGateHarness.SetField(tracker, "_currentSources", null);

        var empty = tracker.GetCurrentSnapshot();
        var rawEmpty = tracker.ReadUnfilteredState().Snapshot;
        Assert.True(empty.IsAvailable);
        Assert.Empty(empty.GameSpeeds);
        Assert.Empty(empty.ClientSpeeds);
        Assert.Equal(0, empty.TotalBytesPerSecond);
        Assert.Equal(0, empty.EntriesInWindow);
        Assert.Empty(rawEmpty.GameSpeeds);
        Assert.Empty(rawEmpty.ClientSpeeds);
        Assert.Null(CacheScanGateHarness.GateOver(tracker).CheckDownloadInProgress());
        var emptyRevision = empty.Revision;

        frame.Revision = 3;
        await tracker.AcceptNativeSnapshotAsync(frame, runId, CancellationToken.None);
        var emptyAgain = tracker.GetCurrentSnapshot();
        var rawEmptyAgain = tracker.ReadUnfilteredState().Snapshot;
        Assert.Empty(emptyAgain.GameSpeeds);
        Assert.Empty(rawEmptyAgain.GameSpeeds);
        Assert.Equal(emptyRevision, emptyAgain.Revision);
        Assert.Equal(emptyRevision, rawEmptyAgain.Revision);
    }

    [Fact]
    public async Task NativeVersionSequenceAndRunIdentityAreValidatedAsync()
    {
        var clock = new MutableClock(new DateTimeOffset(Now));
        var datasources = PrimaryDatasources();
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            TimestampUtc = Now,
            WindowSeconds = 2,
        }, [], clock, datasources);
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();
        var roots = new Dictionary<Guid, Dictionary<string, string>>
        {
            [firstRun] = CacheScanGateHarness.CaptureRoots(datasources),
        };
        CacheScanGateHarness.SetField(tracker, "_currentRunId", firstRun);
        CacheScanGateHarness.SetField(tracker, "_runSources", roots);

        var first = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        first.StreamId = string.Empty;
        first.Revision = 1;
        await tracker.AcceptNativeSnapshotAsync(first, firstRun, CancellationToken.None);
        var accepted = tracker.GetCurrentSnapshot();
        Assert.Equal("public-stream", accepted.StreamId);
        Assert.Equal(1, accepted.Revision);
        Assert.Equal("steam|10.0.0.5|depot:100", Assert.Single(accepted.GameSpeeds).Key);

        var sameSecond = Snapshot(Now.AddSeconds(2), Now.AddSeconds(30), isAvailable: true);
        sameSecond.StreamId = string.Empty;
        sameSecond.Revision = 2;
        sameSecond.GameSpeeds[0].Sources[0].BytesPerSecond = 700;
        sameSecond.GameSpeeds[0].Sources[0].TotalBytes = 1_400;
        sameSecond.GameSpeeds[0].Sources[0].RequestCount = 3;
        await tracker.AcceptNativeSnapshotAsync(sameSecond, firstRun, CancellationToken.None);
        var updated = tracker.GetCurrentSnapshot();
        Assert.Equal(700, Assert.Single(updated.GameSpeeds).BytesPerSecond);
        Assert.Equal(Now.AddSeconds(15), Assert.Single(updated.GameSpeeds).ActiveUntilUtc);

        var invalid = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        invalid.StreamId = string.Empty;
        invalid.Revision = 3;
        // A game row with no sources rejects the whole frame; one bad source row is only skipped.
        invalid.GameSpeeds[0].Sources = [];
        await tracker.AcceptNativeSnapshotAsync(invalid, firstRun, CancellationToken.None);
        Assert.Equal(updated.Revision, tracker.GetCurrentSnapshot().Revision);

        roots[secondRun] = CacheScanGateHarness.CaptureRoots(datasources);
        CacheScanGateHarness.SetField(tracker, "_currentRunId", secondRun);
        await tracker.AcceptNativeSnapshotAsync(first, firstRun, CancellationToken.None);
        Assert.Equal(updated.Revision, tracker.GetCurrentSnapshot().Revision);

        var restarted = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        restarted.StreamId = string.Empty;
        restarted.Revision = 1;
        CacheScanGateHarness.SetField(tracker, "_nativeRevision", 0L);
        await tracker.AcceptNativeSnapshotAsync(restarted, secondRun, CancellationToken.None);
        var afterRestart = tracker.GetCurrentSnapshot();
        Assert.Equal("public-stream", afterRestart.StreamId);
        Assert.True(afterRestart.Revision > updated.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdenticalNativeContentsAdvanceFenceWithoutRepublishingAsync(bool hasActivity)
    {
        var clock = new MutableClock(new DateTimeOffset(Now));
        var datasources = PrimaryDatasources();
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            TimestampUtc = Now,
            WindowSeconds = 2,
        }, [], clock, datasources);
        var runId = Guid.NewGuid();
        CacheScanGateHarness.SetField(tracker, "_currentRunId", runId);
        CacheScanGateHarness.SetField(
            tracker,
            "_runSources",
            new Dictionary<Guid, Dictionary<string, string>>
            {
                [runId] = CacheScanGateHarness.CaptureRoots(datasources),
            });

        var first = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        first.StreamId = string.Empty;
        first.Revision = 1;
        if (!hasActivity)
        {
            first.TotalBytesPerSecond = 0;
            first.EntriesInWindow = 0;
            first.GameSpeeds = [];
            first.ClientSpeeds = [];
        }

        await tracker.AcceptNativeSnapshotAsync(first, runId, CancellationToken.None);

        var second = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        second.StreamId = string.Empty;
        second.Revision = 2;
        second.TimestampUtc = Now.AddSeconds(1);
        if (!hasActivity)
        {
            second.TotalBytesPerSecond = 0;
            second.EntriesInWindow = 0;
            second.GameSpeeds = [];
            second.ClientSpeeds = [];
        }

        clock.UtcNow = new DateTimeOffset(second.TimestampUtc);
        await tracker.AcceptNativeSnapshotAsync(second, runId, CancellationToken.None);

        var current = tracker.GetCurrentSnapshot();
        Assert.Equal(1, current.Revision);
        Assert.Equal(Now, current.TimestampUtc);
        var nativeRevision = typeof(RustSpeedTrackerService).GetField(
            "_nativeRevision",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.Equal(2L, nativeRevision!.GetValue(tracker));

        var publicationField = typeof(RustSpeedTrackerService).GetField(
            "_publication",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var publication = Assert.IsAssignableFrom<Channel<DownloadSpeedSnapshot>>(
            publicationField!.GetValue(tracker));
        Assert.True(publication.Reader.TryRead(out var published));
        Assert.Equal(1, published.Revision);
        Assert.False(publication.Reader.TryRead(out _));
    }

    [Fact]
    public void NativeAliasesUseIndependentCapturedRoots()
    {
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), []);
        var runId = Guid.NewGuid();
        var snapshot = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        snapshot.StreamId = string.Empty;
        snapshot.Revision = 1;
        snapshot.GameSpeeds[0].Sources[0].Datasources = ["A", "B"];

        var other = Snapshot(Now.AddSeconds(2), Now.AddSeconds(20), isAvailable: true).GameSpeeds[0];
        other.ClientIp = "10.0.0.6";
        other.DepotId = 200;
        other.Sources[0].Datasources = ["C"];
        other.Sources[0].DepotIds = [200];
        snapshot.GameSpeeds.Add(other);

        var unknown = Snapshot(Now.AddSeconds(2), Now.AddSeconds(25), isAvailable: true).GameSpeeds[0];
        unknown.ClientIp = "10.0.0.7";
        unknown.DepotId = 300;
        unknown.Sources[0].Datasources = ["unknown"];
        unknown.Sources[0].DepotIds = [300];
        snapshot.GameSpeeds.Add(unknown);

        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "/captured/a",
            ["B"] = "/captured/b",
            ["C"] = "/captured/c",
        };
        var current = new Dictionary<string, string>(captured, StringComparer.OrdinalIgnoreCase);

        var prepared = CacheScanGateHarness.PrepareNativeSnapshot(
            tracker, snapshot, runId, captured, current, Now);
        Assert.NotNull(prepared);
        Assert.Equal(2, prepared!.Count);
        var aliased = prepared.Single(entry => entry.Source.Datasources.Contains("A"));
        Assert.Equal(new[] { "A", "B" }, aliased.Source.Datasources);
        Assert.Equal(Now.AddSeconds(15), aliased.Source.ActiveUntilUtc);
        Assert.Contains(prepared, entry => entry.Source.Datasources.SequenceEqual(["C"]));
        Assert.DoesNotContain(prepared, entry => entry.Source.Datasources.Contains("unknown"));

        current["B"] = "/repointed/b";
        prepared = CacheScanGateHarness.PrepareNativeSnapshot(
            tracker, snapshot, runId, captured, current, Now);
        Assert.NotNull(prepared);
        aliased = prepared!.Single(entry => entry.Source.Datasources.Contains("A"));
        Assert.Equal(new[] { "A" }, aliased.Source.Datasources);
        Assert.Equal(Now.AddSeconds(15), aliased.Source.ActiveUntilUtc);
        Assert.Contains(prepared, entry => entry.Source.Datasources.SequenceEqual(["C"]));

        current.Remove("B");
        prepared = CacheScanGateHarness.PrepareNativeSnapshot(
            tracker, snapshot, runId, captured, current, Now);
        Assert.NotNull(prepared);
        Assert.Equal(new[] { "A" }, prepared!.Single(entry => entry.Source.Datasources.Contains("A")).Source.Datasources);

        current.Remove("A");
        prepared = CacheScanGateHarness.PrepareNativeSnapshot(
            tracker, snapshot, runId, captured, current, Now);
        Assert.NotNull(prepared);
        Assert.Single(prepared!);
        Assert.Equal(new[] { "C" }, prepared[0].Source.Datasources);
    }

    [Fact]
    public async Task OlderConcurrentReadCannotDisplaceNewestQueuedRevision()
    {
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            TimestampUtc = Now,
            WindowSeconds = 2,
        }, []);
        using var publication = CacheScanGateHarness.BlockFirstPublication(tracker);
        var lockField = typeof(RustSpeedTrackerService).GetField(
            "_snapshotLock",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var snapshotLock = Assert.IsType<object>(lockField!.GetValue(tracker));

        var older = Task.Run(() => tracker.QueueSnapshot(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            Revision = 2,
            TimestampUtc = Now,
            WindowSeconds = 2,
        }));
        var firstWriteObserved = publication.WaitForFirstWrite(TimeSpan.FromSeconds(5));
        if (!firstWriteObserved)
        {
            publication.ReleaseFirstWrite();
            await older;
        }
        Assert.True(firstWriteObserved);

        var enteredSnapshotLock = Monitor.TryEnter(snapshotLock);
        if (enteredSnapshotLock)
        {
            Monitor.Exit(snapshotLock);
        }
        var snapshotLockHeld = !enteredSnapshotLock;

        using var newerStarted = new ManualResetEventSlim(false);
        var newer = Task.Run(() =>
        {
            newerStarted.Set();
            tracker.QueueSnapshot(new DownloadSpeedSnapshot
            {
                Version = 2,
                StreamId = "public-stream",
                Revision = 3,
                TimestampUtc = Now,
                WindowSeconds = 2,
            });
        });
        Assert.True(newerStarted.Wait(TimeSpan.FromSeconds(5)));
        var secondWriteObserved = snapshotLockHeld || publication.WaitForSecondWrite(TimeSpan.FromSeconds(5));
        publication.ReleaseFirstWrite();
        await Task.WhenAll(older, newer);

        Assert.True(snapshotLockHeld);
        Assert.True(secondWriteObserved);
        Assert.True(publication.Reader.TryRead(out var queued));
        Assert.Equal(3, queued.Revision);
        Assert.False(publication.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AppMappingConsumesEveryRetainedDepotSourceAsync()
    {
        var clock = new MutableClock(new DateTimeOffset(Now));
        var datasources = PrimaryDatasources();
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            TimestampUtc = Now,
            WindowSeconds = 2,
        }, [], clock, datasources);
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();
        var roots = new Dictionary<Guid, Dictionary<string, string>>
        {
            [firstRun] = CacheScanGateHarness.CaptureRoots(datasources),
        };
        CacheScanGateHarness.SetField(tracker, "_currentRunId", firstRun);
        CacheScanGateHarness.SetField(tracker, "_runSources", roots);

        var unresolved = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        unresolved.StreamId = string.Empty;
        unresolved.Revision = 1;
        var other = Snapshot(Now.AddSeconds(2), Now.AddSeconds(20), isAvailable: true).GameSpeeds[0];
        other.Key = "steam|10.0.0.5|depot:200";
        other.DepotId = 200;
        other.Sources[0].DepotIds = [200];
        unresolved.GameSpeeds.Add(other);
        await tracker.AcceptNativeSnapshotAsync(unresolved, firstRun, CancellationToken.None);
        Assert.Equal(2, tracker.GetCurrentSnapshot().GameSpeeds.Count);

        roots[secondRun] = CacheScanGateHarness.CaptureRoots(datasources);
        CacheScanGateHarness.SetField(tracker, "_currentRunId", secondRun);
        CacheScanGateHarness.SetField(tracker, "_nativeRevision", 0L);
        var mapped = Snapshot(Now.AddSeconds(2), Now.AddSeconds(18), isAvailable: true);
        mapped.StreamId = string.Empty;
        mapped.Revision = 1;
        mapped.GameSpeeds[0].Key = "steam|10.0.0.5|app:10";
        mapped.GameSpeeds[0].GameAppId = 10;
        mapped.GameSpeeds[0].DepotId = 0;
        mapped.GameSpeeds[0].Sources[0].DepotIds = [100, 200];
        await tracker.AcceptNativeSnapshotAsync(mapped, secondRun, CancellationToken.None);

        var game = Assert.Single(tracker.GetCurrentSnapshot().GameSpeeds);
        Assert.Equal("steam|10.0.0.5|app:10", game.Key);
        Assert.Equal(10, game.GameAppId);
        Assert.Equal(Now.AddSeconds(20), game.ActiveUntilUtc);
        Assert.Equal(new long[] { 100, 200 }, Assert.Single(game.Sources).DepotIds);
    }

    [Fact]
    public async Task InvalidSourceRowIsSkippedWithoutDroppingTheRestOfTheSnapshotAsync()
    {
        var datasources = PrimaryDatasources();
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            TimestampUtc = Now,
            WindowSeconds = 2,
        }, [], new MutableClock(new DateTimeOffset(Now)), datasources);
        var logger = new CapturingLogger<RustSpeedTrackerService>();
        CacheScanGateHarness.SetField(tracker, "_logger", logger);
        var runId = Guid.NewGuid();
        CacheScanGateHarness.SetField(tracker, "_currentRunId", runId);
        CacheScanGateHarness.SetField(
            tracker,
            "_runSources",
            new Dictionary<Guid, Dictionary<string, string>>
            {
                [runId] = CacheScanGateHarness.CaptureRoots(datasources),
            });

        var frame = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        frame.StreamId = string.Empty;
        frame.Revision = 1;
        foreach (var clientIp in new[] { "10.0.0.6", "10.0.0.7" })
        {
            var depotZero = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true).GameSpeeds[0];
            depotZero.Key = $"steam|{clientIp}|depot:0";
            depotZero.ClientIp = clientIp;
            depotZero.DepotId = 0;
            depotZero.Sources[0].DepotIds = [0];
            frame.GameSpeeds.Add(depotZero);
        }

        await tracker.AcceptNativeSnapshotAsync(frame, runId, CancellationToken.None);

        var game = Assert.Single(tracker.GetCurrentSnapshot().GameSpeeds);
        Assert.Equal("10.0.0.5", game.ClientIp);
        Assert.Equal(500, game.BytesPerSecond);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void TrackableSourcesAreRebuiltAtMostOncePerMinute()
    {
        var clock = new MutableClock(new DateTimeOffset(Now));
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), [], clock, PrimaryDatasources());
        var currentSources = typeof(RustSpeedTrackerService).GetMethod(
            "CurrentSources",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Dictionary<string, string> Read() => (Dictionary<string, string>)currentSources.Invoke(tracker, null)!;

        var first = Read();
        Assert.Equal(new[] { "primary" }, first.Keys);

        clock.UtcNow = new DateTimeOffset(Now.AddSeconds(59));
        Assert.Same(first, Read());

        clock.UtcNow = new DateTimeOffset(Now.AddSeconds(61));
        var rebuilt = Read();
        Assert.NotSame(first, rebuilt);
        Assert.Equal(first, rebuilt);

        // A clock stepped back must not hold the map until the clock catches up again.
        clock.UtcNow = new DateTimeOffset(Now);
        Assert.NotSame(rebuilt, Read());
    }

    [Fact]
    public async Task LiveRowsReturnAfterTheClockStepsBackAsync()
    {
        var clock = new MutableClock(new DateTimeOffset(Now));
        var datasources = PrimaryDatasources();
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "public-stream",
            TimestampUtc = Now,
            WindowSeconds = 2,
        }, [], clock, datasources);
        var firstRun = Guid.NewGuid();
        var secondRun = Guid.NewGuid();
        var roots = new Dictionary<Guid, Dictionary<string, string>>
        {
            [firstRun] = CacheScanGateHarness.CaptureRoots(datasources),
        };
        CacheScanGateHarness.SetField(tracker, "_currentRunId", firstRun);
        CacheScanGateHarness.SetField(tracker, "_runSources", roots);

        var before = Snapshot(Now.AddSeconds(2), Now.AddSeconds(15), isAvailable: true);
        before.StreamId = string.Empty;
        before.Revision = 1;
        await tracker.AcceptNativeSnapshotAsync(before, firstRun, CancellationToken.None);
        Assert.Equal(500, Assert.Single(tracker.GetCurrentSnapshot().GameSpeeds).BytesPerSecond);

        // The host clock steps back two minutes and the restarted tracker reports the same
        // download at a new rate, next to a download that began after the step.
        var stepped = Now.AddSeconds(-120);
        clock.UtcNow = new DateTimeOffset(stepped);
        roots[secondRun] = CacheScanGateHarness.CaptureRoots(datasources);
        CacheScanGateHarness.SetField(tracker, "_currentRunId", secondRun);
        CacheScanGateHarness.SetField(tracker, "_nativeRevision", 0L);
        void MoveTo(GameSpeedInfo game)
        {
            game.FirstSeenUtc = stepped;
            game.LastSeenUtc = stepped;
            game.Sources[0].FirstSeenUtc = stepped;
            game.Sources[0].LastSeenUtc = stepped;
        }

        var after = Snapshot(stepped.AddSeconds(2), stepped.AddSeconds(15), isAvailable: true);
        after.StreamId = string.Empty;
        after.Revision = 1;
        after.TimestampUtc = stepped;
        var resumed = after.GameSpeeds[0];
        MoveTo(resumed);
        resumed.BytesPerSecond = 900;
        resumed.Sources[0].BytesPerSecond = 900;
        var started = Snapshot(stepped.AddSeconds(2), stepped.AddSeconds(15), isAvailable: true).GameSpeeds[0];
        MoveTo(started);
        started.Key = "steam|10.0.0.6|depot:200";
        started.ClientIp = "10.0.0.6";
        started.DepotId = 200;
        started.Sources[0].DepotIds = [200];
        after.GameSpeeds.Add(started);
        await tracker.AcceptNativeSnapshotAsync(after, secondRun, CancellationToken.None);

        var current = tracker.GetCurrentSnapshot();
        Assert.Equal(900, current.GameSpeeds.Single(game => game.ClientIp == "10.0.0.5").BytesPerSecond);
        Assert.Contains(current.GameSpeeds, game => game.ClientIp == "10.0.0.6");
    }

    [Theory]
    [InlineData(LogFileLockKind.Rows)]
    [InlineData(LogFileLockKind.Rewrite)]
    public async Task LogStepStopsTheChildAndRestartsItWithTheSnapshotKeptAsync(LogFileLockKind kind)
    {
        var root = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false);
        var logger = new CapturingLogger<RustSpeedTrackerService>();
        using var stop = new CancellationTokenSource();
        var (tracker, loop) = StartLoop(harness.Owner, ChildScript(root, exitsAtOnce: false), logger, stop.Token);
        var processField = typeof(RustSpeedTrackerService).GetField(
            "_rustProcess",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Process? Child() => (Process?)processField.GetValue(tracker);
        Assert.True(SpinWait.SpinUntil(() => Child() is not null, TimeSpan.FromSeconds(20)));
        using var first = Process.GetProcessById(Child()!.Id);

        // Stand in for the first child's parsed snapshot: the tracker is publishing rows.
        var busy = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(busy);
        // Long enough for the step to be granted first, short enough to run out during it.
        var busyGame = Assert.Single(busy.GameSpeeds);
        busyGame.ActiveUntilUtc = busy.TimestampUtc.AddSeconds(3);
        busyGame.Sources[0].MeasuredUntilUtc = busyGame.ActiveUntilUtc;
        busyGame.Sources[0].ActiveUntilUtc = busyGame.ActiveUntilUtc;
        var snapshotLock = typeof(RustSpeedTrackerService).GetField(
            "_snapshotLock",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tracker)!;
        lock (snapshotLock)
        {
            CacheScanGateHarness.SetField(tracker, "_currentSnapshot", busy);
            CacheScanGateHarness.SetField(tracker, "_unreportedSinceUtc", null);
            CacheScanGateHarness.SetField(tracker, "_agingUtc", busy.TimestampUtc);
        }

        var gate = CacheScanGateHarness.GateOver(tracker);

        // The step is granted only once the tracker has stopped its child and said so.
        var stepLock = await harness.Owner
            .LockLogFilesAsync(null, OperationType.GameRemoval, kind, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Null(Child());
        Assert.True(first.HasExited);
        var duringStep = tracker.ReadUnfilteredState();
        Assert.Null(duringStep.UnreportedSinceUtc);
        Assert.True(duringStep.Snapshot.IsAvailable);
        Assert.NotEmpty(duringStep.Snapshot.GameSpeeds);

        // The row's own window has run out, but the stopped child cannot report a download that is
        // still running, so the gate stays shut while the step holds the logs.
        await Task.Delay(TimeSpan.FromSeconds(4));
        Assert.NotNull(gate.CheckDownloadInProgress());

        await stepLock.DisposeAsync();
        Assert.True(SpinWait.SpinUntil(
            () => Child() is { } next && next.Id != first.Id,
            TimeSpan.FromSeconds(20)));
        var restarted = tracker.ReadUnfilteredState();
        Assert.Null(restarted.UnreportedSinceUtc);
        Assert.True(restarted.Snapshot.IsAvailable);
        // No failure was counted, so no restart delay was applied.
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        // The new child reports nothing, so aging resumes and the row runs out.
        Assert.True(SpinWait.SpinUntil(() => gate.CheckDownloadInProgress() is null, TimeSpan.FromSeconds(10)));

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildEndGivesTheLogsBackBeforeTheRestartDelayAsync(bool childStarts)
    {
        var root = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false);
        var running = typeof(OperationStateService).GetField(
            "_speedTrackerRunning",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var runningWhenReported = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CapturingLogger<RustSpeedTrackerService>
        {
            // Logged after the run ended and right before the restart delay starts.
            OnLogged = entry =>
            {
                if (entry.Level >= LogLevel.Warning)
                {
                    runningWhenReported.TrySetResult((bool)running.GetValue(harness.Owner)!);
                }
            },
        };
        var executable = childStarts
            ? ChildScript(root, exitsAtOnce: true)
            : Path.Combine(root, "missing-tracker");
        using var stop = new CancellationTokenSource();
        var (_, loop) = StartLoop(harness.Owner, executable, logger, stop.Token);

        Assert.False(await runningWhenReported.Task.WaitAsync(TimeSpan.FromSeconds(20)));
        await using (await harness.Owner
            .LockLogFilesAsync(null, OperationType.GameRemoval, LogFileLockKind.Rewrite, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20)))
        {
            await stop.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(20));
        }
    }

    // Runs the tracker's real restart loop against a stand-in executable rather than the native
    // binary, taking turns on the logs with the given owner.
    private static (RustSpeedTrackerService Tracker, Task Loop) StartLoop(
        OperationStateService owner,
        string executable,
        CapturingLogger<RustSpeedTrackerService> logger,
        CancellationToken stoppingToken)
    {
        var tracker = CacheScanGateHarness.TrackerWith(
            new DownloadSpeedSnapshot(),
            [],
            datasources: PrimaryDatasources());
        CacheScanGateHarness.SetField(tracker, "_logger", logger);
        CacheScanGateHarness.SetField(tracker, "_processManager", new ProcessManager(NullLogger<ProcessManager>.Instance));
        CacheScanGateHarness.SetField(tracker, "_operationStateService", owner);
        CacheScanGateHarness.SetField(tracker, "_rustExecutablePath", executable);
        var loop = (Task)typeof(RustSpeedTrackerService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tracker, [stoppingToken])!;
        return (tracker, loop);
    }

    // A stand-in for the native tracker: it either exits at once, or prints an empty line about
    // once a second until it is killed.
    private static string ChildScript(string root, bool exitsAtOnce)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(root, "tracker.cmd");
            File.WriteAllText(script, exitsAtOnce
                ? "@echo off\r\nexit /b 0\r\n"
                : "@echo off\r\n:loop\r\necho.\r\nping -n 2 127.0.0.1 >nul\r\ngoto loop\r\n");
            return script;
        }

        var shell = Path.Combine(root, "tracker.sh");
        File.WriteAllText(shell, exitsAtOnce
            ? "#!/bin/sh\nexit 0\n"
            : "#!/bin/sh\nwhile true; do echo; sleep 1; done\n");
        File.SetUnixFileMode(shell, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return shell;
    }

    private static DatasourceService PrimaryDatasources() =>
        CacheScanGateHarness.DatasourceServiceWith(
            ("primary",
             Path.Combine(FixtureRoot, "primary-cache"),
             Path.Combine(FixtureRoot, "primary-logs"),
             true,
             "monolithic"));

    private static DatasourceService CurrentDatasources(string change, bool invalidateC)
    {
        var invalidatesA = !string.Equals(change, "none", StringComparison.Ordinal);
        var disablesA = string.Equals(change, "disabled", StringComparison.Ordinal);
        var repointsA = string.Equals(change, "repointed", StringComparison.Ordinal);
        var makesAUntrackable = string.Equals(change, "untrackable", StringComparison.Ordinal);
        var disablesC = invalidateC && disablesA;
        var repointsC = invalidateC && repointsA;
        var makesCUntrackable = invalidateC && makesAUntrackable;

        return CacheScanGateHarness.DatasourceServiceWith(
            ("A",
             Path.Combine(FixtureRoot, "A-cache"),
             Path.Combine(FixtureRoot, repointsA && invalidatesA ? "A-logs-repointed" : "A-logs"),
             !disablesA || !invalidatesA,
             makesAUntrackable && invalidatesA ? "auto" : "monolithic"),
            ("C",
             Path.Combine(FixtureRoot, "C-cache"),
             Path.Combine(FixtureRoot, repointsC ? "C-logs-repointed" : "C-logs"),
             !disablesC,
             makesCUntrackable ? "auto" : "monolithic"));
    }

    internal static DownloadSpeedSnapshot Snapshot(
        DateTime measuredUntilUtc,
        DateTime activeUntilUtc,
        bool isAvailable)
    {
        var source = new DownloadSource
        {
            Datasources = ["primary"],
            DepotIds = [100],
            FirstSeenUtc = Now,
            LastSeenUtc = Now,
            MeasuredUntilUtc = measuredUntilUtc,
            ActiveUntilUtc = activeUntilUtc,
            BytesPerSecond = 500,
            TotalBytes = 1_000,
            RequestCount = 2,
            CacheHitBytes = 750,
            CacheMissBytes = 250,
        };
        return new DownloadSpeedSnapshot
        {
            Version = 2,
            StreamId = "activity-stream",
            Revision = 4,
            TimestampUtc = Now,
            IsAvailable = isAvailable,
            WindowSeconds = 2,
            TotalBytesPerSecond = 500,
            EntriesInWindow = 2,
            GameSpeeds =
            [
                new GameSpeedInfo
                {
                    Key = "steam|10.0.0.5|depot:100",
                    DepotId = 100,
                    Service = "steam",
                    ClientIp = "10.0.0.5",
                    BytesPerSecond = 500,
                    TotalBytes = 1_000,
                    RequestCount = 2,
                    CacheHitBytes = 750,
                    CacheMissBytes = 250,
                    FirstSeenUtc = Now,
                    LastSeenUtc = Now,
                    ActiveUntilUtc = activeUntilUtc,
                    Sources = [source],
                },
            ],
            ClientSpeeds =
            [
                new ClientSpeedInfo
                {
                    ClientIp = "10.0.0.5",
                    BytesPerSecond = 500,
                    TotalBytes = 1_000,
                    ActiveGames = 1,
                    CacheHitBytes = 750,
                    CacheMissBytes = 250,
                    ActiveUntilUtc = activeUntilUtc,
                },
            ],
        };
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
