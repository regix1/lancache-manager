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
    // The client the harness puts in a busy snapshot, named here so a test can hide that one.
    private const string BusyClientIp = "10.0.0.5";

    [Fact]
    public void DownloadsStoppingAnnouncesOnce()
    {
        Assert.Equal(1, CountAnnouncements([Busy(), Idle()], []));
    }

    [Fact]
    public void StayingIdleAnnouncesNothingFurther()
    {
        Assert.Equal(1, CountAnnouncements([Busy(), Idle(), Idle(), Idle()], []));
    }

    [Fact]
    public void DownloadsStartingAnnouncesNothing()
    {
        Assert.Equal(0, CountAnnouncements([Idle(), Busy()], []));
    }

    [Fact]
    public void EachStopIsAnnouncedSeparately()
    {
        Assert.Equal(2, CountAnnouncements([Busy(), Idle(), Busy(), Idle()], []));
    }

    /// <summary>
    /// Hiding the only client downloading empties the client-visible snapshot while the cache is
    /// still being written to. Reading that projection instead of the raw snapshot would treat the
    /// hidden download as idle and announce the end of a download that is still running.
    /// </summary>
    [Fact]
    public void AHiddenClientDownloadingStillCountsAsBusy()
    {
        var hiddenOnly = Busy();
        var visible = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            hiddenOnly, [BusyClientIp], EvictedDataMode.Show.ToWireString());

        Assert.False(visible.HasActiveDownloads);
        Assert.True(hiddenOnly.HasActiveDownloads);

        Assert.Equal(1, CountAnnouncements([hiddenOnly, Idle()], [BusyClientIp]));
    }

    /// <summary>
    /// Feeds the snapshots to the tracker in order and counts the announcements they produce.
    /// </summary>
    private static int CountAnnouncements(
        IReadOnlyList<DownloadSpeedSnapshot> snapshots, IReadOnlyCollection<string> hiddenClientIps)
    {
        var tracker = CacheScanGateHarness.TrackerWith(new DownloadSpeedSnapshot(), hiddenClientIps);
        var announcements = 0;

        void CountOne() => announcements++;

        var otherHandlers = TakeOverDownloadsEnded();
        try
        {
            RustSpeedTrackerService.DownloadsEnded += CountOne;
            foreach (var snapshot in snapshots)
            {
                tracker.AnnounceDownloadsEndedIfStopped(snapshot);
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
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        return snapshot;
    }

    private static DownloadSpeedSnapshot Idle() => new();

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
}
