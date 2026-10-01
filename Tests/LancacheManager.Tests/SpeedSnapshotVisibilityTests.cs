using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the shared client-visible speed snapshot semantics: hidden clients are absent from
/// BOTH transports (the SignalR broadcast and the REST endpoint use the same builder), totals
/// are recomputed from the retained entries, and the raw tracker snapshot is never mutated.
/// </summary>
public sealed class SpeedSnapshotVisibilityTests
{
    private static readonly List<string> HiddenClients = ["10.0.0.9"];

    private static DownloadSpeedSnapshot BuildRawSnapshot() => new()
    {
        TimestampUtc = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc),
        StreamId = "visibility-stream",
        Revision = 9,
        IsAvailable = true,
        WindowSeconds = 4,
        // Raw totals are deliberately wrong so a test failure proves recomputation.
        TotalBytesPerSecond = 999_999,
        EntriesInWindow = 999,
        ClientSpeeds =
        [
            new ClientSpeedInfo { ClientIp = "10.0.0.1", BytesPerSecond = 100 },
            new ClientSpeedInfo { ClientIp = "10.0.0.9", BytesPerSecond = 50 }
        ],
        GameSpeeds =
        [
            new GameSpeedInfo { ClientIp = "10.0.0.1", Service = "steam", RequestCount = 3, BytesPerSecond = 100 },
            new GameSpeedInfo { ClientIp = "10.0.0.9", Service = "steam", RequestCount = 5 },
            new GameSpeedInfo { ClientIp = "", Service = "wsus", RequestCount = 2 },
            new GameSpeedInfo { ClientIp = "10.0.0.1", Service = "epic", RequestCount = 4, IsEvicted = true }
        ]
    };

    [Fact]
    public void HiddenClientsAreRemovedAndTotalsRecomputed()
    {
        var visible = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            BuildRawSnapshot(), HiddenClients, EvictedDataMode.Show.ToWireString());

        Assert.DoesNotContain(visible.ClientSpeeds, c => c.ClientIp == "10.0.0.9");
        Assert.DoesNotContain(visible.GameSpeeds, g => g.ClientIp == "10.0.0.9");

        // Totals reflect only the retained entries, never the raw tracker totals.
        Assert.Equal(100, visible.TotalBytesPerSecond);
        Assert.Equal(3 + 2 + 4, visible.EntriesInWindow);
    }

    [Fact]
    public void GameEntriesWithoutClientIpAreRetained()
    {
        var visible = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            BuildRawSnapshot(), HiddenClients, EvictedDataMode.Show.ToWireString());

        Assert.Contains(visible.GameSpeeds, g => g.Service == "wsus" && g.ClientIp == "");
    }

    [Theory]
    [InlineData(EvictedDataMode.Hide)]
    [InlineData(EvictedDataMode.Remove)]
    public void EvictedEntriesAreExcludedInHideAndRemoveModes(EvictedDataMode mode)
    {
        var visible = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            BuildRawSnapshot(), HiddenClients, mode.ToWireString());

        Assert.DoesNotContain(visible.GameSpeeds, g => g.IsEvicted);
        Assert.DoesNotContain(visible.GameSpeeds, g => g.Service == "epic");
        Assert.Equal(3 + 2, visible.EntriesInWindow);
    }

    [Fact]
    public void ShowCleanClearsTheEvictedFlagWithoutMutatingTheRawSnapshot()
    {
        var raw = BuildRawSnapshot();

        var visible = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            raw, HiddenClients, EvictedDataMode.ShowClean.ToWireString());

        Assert.All(visible.GameSpeeds, g => Assert.False(g.IsEvicted));
        Assert.True(raw.GameSpeeds.Single(g => g.Service == "epic").IsEvicted,
            "the raw tracker snapshot must never be mutated by a display rewrite");
    }

    [Fact]
    public void RawSnapshotListsAndTotalsAreUntouched()
    {
        var raw = BuildRawSnapshot();

        RustSpeedTrackerService.BuildClientVisibleSnapshot(
            raw, HiddenClients, EvictedDataMode.Hide.ToWireString());

        Assert.Equal(2, raw.ClientSpeeds.Count);
        Assert.Equal(4, raw.GameSpeeds.Count);
        Assert.Equal(999_999, raw.TotalBytesPerSecond);
        Assert.Equal(999, raw.EntriesInWindow);
    }

    [Fact]
    public void RestAndPublishedProjectionUseTheSameSnapshot()
    {
        var raw = BuildRawSnapshot();
        var tracker = CacheScanGateHarness.TrackerWith(raw, HiddenClients);
        var rest = tracker.GetCurrentSnapshot();
        var published = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            raw, HiddenClients, EvictedDataMode.Show.ToWireString());

        Assert.Equal(published.StreamId, rest.StreamId);
        Assert.Equal(published.Revision, rest.Revision);
        Assert.Equal(published.IsAvailable, rest.IsAvailable);
        Assert.Equal(published.TotalBytesPerSecond, rest.TotalBytesPerSecond);
        Assert.Equal(
            published.GameSpeeds.Select(game => (game.Service, game.ClientIp, game.RequestCount)),
            rest.GameSpeeds.Select(game => (game.Service, game.ClientIp, game.RequestCount)));
        Assert.DoesNotContain(rest.GameSpeeds, game => game.ClientIp == "10.0.0.9");
    }
}
