using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Groups every class that raises <see cref="RustSpeedTrackerService.DownloadsEnded"/>. The event
/// is static and therefore process-wide, so a raise from one class would be counted by another if
/// the two ran at the same time. Classes in one collection run one after the other, while the
/// collection still runs in parallel with the rest of the suite.
/// </summary>
[CollectionDefinition(nameof(DownloadsEndedEventCollection))]
public sealed class DownloadsEndedEventCollection
{
}

/// <summary>
/// Covers the edge that tells the rest of the server the last download finished: it is announced
/// once when the download set empties, it stays quiet while nothing is downloading, and it reads
/// the unfiltered set, so a download by a hidden client still counts as busy.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public class DownloadsEndedAnnouncementTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc);
    private static readonly string FixtureRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(DownloadsEndedAnnouncementTests));

    // The client the harness puts in a busy snapshot, named here so a test can hide that one.
    private const string BusyClientIp = "10.0.0.5";

    [Fact]
    public async Task DownloadsStoppingAnnouncesOnce()
    {
        Assert.Equal(1, await CountAnnouncements([Busy(), Idle()], []));
    }

    [Fact]
    public async Task StayingIdleAnnouncesNothingFurther()
    {
        Assert.Equal(1, await CountAnnouncements([Busy(), Idle(), Idle(), Idle()], []));
    }

    [Fact]
    public async Task DownloadsStartingAnnouncesNothing()
    {
        Assert.Equal(0, await CountAnnouncements([Idle(), Busy()], []));
    }

    [Fact]
    public async Task EachStopIsAnnouncedSeparately()
    {
        Assert.Equal(2, await CountAnnouncements([Busy(), Idle(), Busy(), Idle()], []));
    }

    /// <summary>
    /// Hiding the only client downloading empties the client-visible snapshot while the cache is
    /// still being written to. Reading that projection instead of the raw snapshot would treat the
    /// hidden download as idle and announce the end of a download that is still running.
    /// </summary>
    [Fact]
    public async Task AHiddenClientDownloadingStillCountsAsBusy()
    {
        var firstBusy = Busy();
        var secondBusy = Busy();
        var changedSource = Assert.Single(Assert.Single(secondBusy.GameSpeeds).Sources);
        changedSource.BytesPerSecond = 700;
        changedSource.TotalBytes = 1_400;
        changedSource.RequestCount = 3;
        var counts = new List<int>();

        var finalCount = await CountAnnouncements(
            [firstBusy, secondBusy, Idle()],
            [BusyClientIp],
            (tracker, count, index) =>
            {
                counts.Add(count);
                if (index == 1)
                {
                    Assert.Empty(tracker.GetCurrentSnapshot().GameSpeeds);
                    Assert.True(tracker.ReadUnfilteredState().Snapshot.HasActiveDownloads);
                }
            });

        Assert.Equal(new[] { 0, 0, 1 }, counts);
        Assert.Equal(1, finalCount);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void BoundaryAnnouncesOnlyWhileReportingIsHealthy(bool isAvailable, int expected)
    {
        var activeUntilUtc = Now.AddSeconds(15);
        var clock = new MutableClock(new DateTimeOffset(activeUntilUtc));
        var snapshot = SpeedActivityTests.Snapshot(Now.AddSeconds(2), activeUntilUtc, isAvailable);
        var tracker = CacheScanGateHarness.TrackerWith(snapshot, [], clock);
        CacheScanGateHarness.SetField(tracker, "_previousHadUnfilteredActivity", true);
        CacheScanGateHarness.SetField(tracker, "_previousHadActivity", true);
        CacheScanGateHarness.SetField(tracker, "_edgeRevision", snapshot.Revision);
        var announcements = 0;

        void CountOne() => announcements++;

        var otherHandlers = TakeOverDownloadsEnded();
        try
        {
            RustSpeedTrackerService.DownloadsEnded += CountOne;
            _ = tracker.GetCurrentSnapshot();
        }
        finally
        {
            RestoreDownloadsEnded(otherHandlers);
        }

        Assert.Equal(expected, announcements);
        Assert.Empty(tracker.ReadUnfilteredState().Snapshot.GameSpeeds);
    }

    /// <summary>
    /// Feeds the snapshots to the tracker in order and counts the announcements they produce.
    /// </summary>
    private static async Task<int> CountAnnouncements(
        IReadOnlyList<DownloadSpeedSnapshot> snapshots,
        IReadOnlyCollection<string> hiddenClientIps,
        Action<RustSpeedTrackerService, int, int>? afterSnapshot = null)
    {
        var datasources = CacheScanGateHarness.DatasourceServiceWith(
            ("primary",
             Path.Combine(FixtureRoot, "cache"),
             Path.Combine(FixtureRoot, "logs"),
             true,
             "monolithic"));
        var clock = new MutableClock(new DateTimeOffset(Now.AddSeconds(1)));
        var tracker = CacheScanGateHarness.TrackerWith(
            new DownloadSpeedSnapshot
            {
                StreamId = "event-stream",
                TimestampUtc = Now,
            },
            hiddenClientIps,
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
        var announcements = 0;

        void CountOne() => announcements++;

        var otherHandlers = TakeOverDownloadsEnded();
        try
        {
            RustSpeedTrackerService.DownloadsEnded += CountOne;
            for (var index = 0; index < snapshots.Count; index++)
            {
                var snapshot = snapshots[index];
                snapshot.Version = 2;
                snapshot.StreamId = string.Empty;
                snapshot.Revision = index + 1;
                snapshot.TimestampUtc = Now.AddMilliseconds(index);
                snapshot.IsAvailable = true;
                snapshot.WindowSeconds = 2;
                await tracker.AcceptNativeSnapshotAsync(snapshot, runId, CancellationToken.None);
                afterSnapshot?.Invoke(tracker, announcements, index);
            }
        }
        finally
        {
            RestoreDownloadsEnded(otherHandlers);
        }

        return announcements;
    }

    private static DownloadSpeedSnapshot Busy()
    {
        return SpeedActivityTests.Snapshot(
            Now.AddSeconds(2),
            Now.AddSeconds(15),
            isAvailable: true);
    }

    private static DownloadSpeedSnapshot Idle() => new()
    {
        IsAvailable = true,
    };

    /// <summary>
    /// Takes the process-wide event over for the length of one run and returns whatever was
    /// subscribed, so the real handlers registered by other tests neither see these raises nor
    /// stay detached afterwards.
    /// </summary>
    private static Delegate? TakeOverDownloadsEnded()
    {
        var field = DownloadsEndedField();
        var subscribed = (Delegate?)field.GetValue(null);
        field.SetValue(null, null);
        return subscribed;
    }

    private static void RestoreDownloadsEnded(Delegate? subscribed)
        => DownloadsEndedField().SetValue(null, subscribed);

    // The event exposes only add and remove, so the handler list is reached through its field.
    private static FieldInfo DownloadsEndedField()
        => typeof(RustSpeedTrackerService).GetField(
            nameof(RustSpeedTrackerService.DownloadsEnded),
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    private sealed class MutableClock(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
