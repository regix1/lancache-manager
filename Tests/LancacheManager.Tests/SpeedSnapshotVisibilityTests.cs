using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the shared client-visible speed snapshot semantics: hidden clients and prefill
/// traffic are absent from BOTH transports (the SignalR broadcast and the REST endpoint use
/// the same builder), totals are recomputed from the retained entries, and the raw tracker
/// snapshot is never mutated.
/// </summary>
public sealed class SpeedSnapshotVisibilityTests
{
    private static readonly List<string> HiddenClients = ["10.0.0.9"];

    private static DownloadSpeedSnapshot BuildRawSnapshot() => new()
    {
        TimestampUtc = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc),
        WindowSeconds = 4,
        // Raw totals are deliberately wrong so a test failure proves recomputation.
        TotalBytesPerSecond = 999_999,
        EntriesInWindow = 999,
        ClientSpeeds =
        [
            new ClientSpeedInfo { ClientIp = "10.0.0.1", BytesPerSecond = 100 },
            new ClientSpeedInfo { ClientIp = "10.0.0.9", BytesPerSecond = 50 },
            new ClientSpeedInfo { ClientIp = "Prefill", BytesPerSecond = 25 }
        ],
        GameSpeeds =
        [
            new GameSpeedInfo { ClientIp = "10.0.0.1", Service = "steam", RequestCount = 3, BytesPerSecond = 100 },
            new GameSpeedInfo { ClientIp = "10.0.0.9", Service = "steam", RequestCount = 5 },
            new GameSpeedInfo { ClientIp = "prefill", Service = "steam", RequestCount = 7 },
            new GameSpeedInfo { ClientIp = "", Service = "wsus", RequestCount = 2 },
            new GameSpeedInfo { ClientIp = "10.0.0.1", Service = "epic", RequestCount = 4, IsEvicted = true }
        ]
    };

    [Fact]
    public void HiddenAndPrefillClientsAreRemovedAndTotalsRecomputed()
    {
        var visible = RustSpeedTrackerService.BuildClientVisibleSnapshot(
            BuildRawSnapshot(), HiddenClients, EvictedDataMode.Show.ToWireString());

        Assert.DoesNotContain(visible.ClientSpeeds, c => c.ClientIp == "10.0.0.9");
        Assert.DoesNotContain(visible.ClientSpeeds, c => string.Equals(c.ClientIp, "prefill", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(visible.GameSpeeds, g => g.ClientIp == "10.0.0.9");
        Assert.DoesNotContain(visible.GameSpeeds, g => string.Equals(g.ClientIp, "prefill", StringComparison.OrdinalIgnoreCase));

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

        Assert.Equal(3, raw.ClientSpeeds.Count);
        Assert.Equal(5, raw.GameSpeeds.Count);
        Assert.Equal(999_999, raw.TotalBytesPerSecond);
        Assert.Equal(999, raw.EntriesInWindow);
    }

    [Fact]
    public void SignalRBroadcastAndRestEndpointUseTheSharedBuilder()
    {
        var trackerSource = ReadSource(Path.Combine("Core", "Services", "RustSpeedTrackerService.cs"));

        var buildForBroadcast = trackerSource.IndexOf("var visibleSnapshot = BuildClientVisibleSnapshot(", StringComparison.Ordinal);
        var broadcast = trackerSource.IndexOf("NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, visibleSnapshot)", StringComparison.Ordinal);
        Assert.True(buildForBroadcast >= 0, "the broadcast must build the client-visible projection");
        Assert.True(broadcast > buildForBroadcast, "the hub send must use the client-visible projection");

        var controllerSource = ReadSource(Path.Combine("Controllers", "SpeedsController.cs"));
        Assert.Contains("_speedTrackerService.GetCurrentSnapshot()", controllerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHiddenClientIps", GetCurrentSpeedsSlice(controllerSource), StringComparison.Ordinal);
    }

    private static string GetCurrentSpeedsSlice(string controllerSource)
    {
        var start = controllerSource.IndexOf("GetCurrentSpeeds()", StringComparison.Ordinal);
        var end = controllerSource.IndexOf("GetSpeedHistoryAsync", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return controllerSource[start..end];
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine(root, "Api", "LancacheManager", relativePath));
    }
}
