using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Covers what makes a newly started download appear on the Downloads page promptly.
/// </summary>
/// <remarks>
/// The speed events that follow a download carry rates, not rows, so the only signal that re-read
/// the DB-backed download list was the edge where the LAST download stopped. A download that began
/// while the page was open therefore showed no row until it had finished. The list is now refreshed
/// whenever the set of active downloads changes, which is what these lock: a download joining is a
/// different set, and the same downloads continuing are not. [51]
/// </remarks>
public class ActiveDownloadSetChangeTests
{
    [Fact]
    public void ASecondDownloadStarting_ChangesTheSet_SoTheListIsReRead()
    {
        var oneDownload = SnapshotOf(346110);
        var twoDownloads = SnapshotOf(346110, 1672970);

        Assert.NotEqual(
            RustSpeedTrackerService.ActiveDepotSignature(oneDownload),
            RustSpeedTrackerService.ActiveDepotSignature(twoDownloads));
    }

    [Fact]
    public void TheFirstDownloadStarting_ChangesTheSet_SoTheListIsReRead()
    {
        // The case the user hit: nothing was downloading, one starts, and the row has to arrive
        // without waiting for that download to finish.
        Assert.NotEqual(
            RustSpeedTrackerService.ActiveDepotSignature(new DownloadSpeedSnapshot()),
            RustSpeedTrackerService.ActiveDepotSignature(SnapshotOf(346110)));
    }

    [Fact]
    public void TheSameDownloadsContinuing_DoNotChangeTheSet_SoNothingIsReRead()
    {
        // A long download reports a snapshot roughly every second. Re-reading the list on each one
        // would put a query behind every tick of a download that can run for hours.
        Assert.Equal(
            RustSpeedTrackerService.ActiveDepotSignature(SnapshotOf(346110, 1672970)),
            RustSpeedTrackerService.ActiveDepotSignature(SnapshotOf(346110, 1672970)));
    }

    [Fact]
    public void TheSameDownloadsInADifferentOrder_AreTheSameSet()
    {
        // Nothing promises the tracker orders its per-game list, and an order change is not a change
        // in what is downloading.
        Assert.Equal(
            RustSpeedTrackerService.ActiveDepotSignature(SnapshotOf(346110, 1672970)),
            RustSpeedTrackerService.ActiveDepotSignature(SnapshotOf(1672970, 346110)));
    }

    [Fact]
    public void TwoUnmappedDownloads_AreTellableApart()
    {
        // Signing on the game name instead would read both of these as one set, because a name stays
        // null until the download is mapped, and the second download would never refresh the list.
        var first = new DownloadSpeedSnapshot
        {
            GameSpeeds = [new GameSpeedInfo { DepotId = 111, GameName = null }]
        };
        var both = new DownloadSpeedSnapshot
        {
            GameSpeeds =
            [
                new GameSpeedInfo { DepotId = 111, GameName = null },
                new GameSpeedInfo { DepotId = 222, GameName = null }
            ]
        };

        Assert.NotEqual(
            RustSpeedTrackerService.ActiveDepotSignature(first),
            RustSpeedTrackerService.ActiveDepotSignature(both));
    }

    [Fact]
    public void AChangedSet_RefreshesTwice_SoARowCommittedAMomentLaterIsStillPickedUp()
    {
        // The speed tracker and the log processor are separate binaries tailing the same logs, so a
        // download can appear in a speed snapshot before its row is committed. Refreshing only on
        // the change itself can read the list too early and find nothing, and the set does not
        // change a second time, so that download would never arrive. The follow-up is what makes the
        // FIRST appearance of a row reliable rather than only the updates to one that exists. [52]
        var refreshes = RefreshesFor([Idle, () => SnapshotOf(346110), () => SnapshotOf(346110), () => SnapshotOf(346110)]);

        // One for the change, one follow-up, then silence while the same download continues.
        Assert.Equal([false, true, true, false], refreshes);
    }

    [Fact]
    public void AnUnchangingSet_RefreshesNothing()
    {
        var refreshes = RefreshesFor([() => SnapshotOf(346110), () => SnapshotOf(346110), () => SnapshotOf(346110)]);

        Assert.Equal([true, true, false], refreshes);
    }

    /// <summary>
    /// Replays the service's refresh decision over a run of snapshots: refresh when the active set
    /// changed, plus one follow-up on the snapshot after a change.
    /// </summary>
    private static List<bool> RefreshesFor(IReadOnlyList<Func<DownloadSpeedSnapshot>> snapshots)
    {
        var previous = string.Empty;
        var followUp = false;
        var refreshes = new List<bool>();

        foreach (var next in snapshots)
        {
            var signature = RustSpeedTrackerService.ActiveDepotSignature(next());
            var changed = !string.Equals(signature, previous, StringComparison.Ordinal);

            refreshes.Add(changed || followUp);
            followUp = changed;
            previous = signature;
        }

        return refreshes;
    }

    private static DownloadSpeedSnapshot Idle() => new();

    private static DownloadSpeedSnapshot SnapshotOf(params long[] depotIds) => new()
    {
        GameSpeeds = depotIds.Select(depotId => new GameSpeedInfo { DepotId = depotId }).ToList()
    };
}
