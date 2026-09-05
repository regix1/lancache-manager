using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the scan the game detection schedule actually runs, which is a different question from the
/// value the dropdown stores. Every install that predates this setting has no value in state.json,
/// and the answer there has to be the full scan rather than whichever mode happened to sort first.
/// The hybrid assertions mirror the depot crawl's week in SteamKit2HybridScanModeTests, because the
/// two modes are the same rule read off different timestamps.
/// </summary>
public sealed class GameDetectionScanModeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root;

    public GameDetectionScanModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-detect-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // A state.json written before this setting existed carries no key at all, and that file is what
    // every upgrading install starts from. Deserializing the absent key as anything but Full would
    // silently move those installs onto a scan they never chose.
    [Fact]
    public void AStateFileWithoutTheSettingReadsAsFull()
    {
        var state = JsonSerializer.Deserialize<AppState>("""{"setupCompleted":true}""");

        Assert.NotNull(state);
        Assert.Equal(GameDetectionScanMode.Full, state!.GameDetectionScanMode);
        Assert.Null(state.GameDetectionLastFullScanUtc);
    }

    [Fact]
    public void AFreshInstallReadsAsFull()
    {
        var state = StateTestMethods.CreateStateService(_root);

        Assert.Equal(GameDetectionScanMode.Full, state.GetGameDetectionScanMode());
        Assert.Null(state.GetGameDetectionLastFullScan());
    }

    [Theory]
    [InlineData(GameDetectionScanMode.Full)]
    [InlineData(GameDetectionScanMode.Incremental)]
    [InlineData(GameDetectionScanMode.Hybrid)]
    public void EveryModeSurvivesARestart(GameDetectionScanMode mode)
    {
        var writer = StateTestMethods.CreateStateService(_root);
        writer.SetGameDetectionScanMode(mode);

        // A second instance over the same directory is the restarted container reading state.json.
        var reader = StateTestMethods.CreateStateService(_root);

        Assert.Equal(mode, reader.GetGameDetectionScanMode());
    }

    // The frontend union is camelCase, so a PascalCase member name on the wire would arrive as a mode
    // the dropdown cannot match and would render as an empty selection.
    [Theory]
    [InlineData(GameDetectionScanMode.Full, "\"full\"")]
    [InlineData(GameDetectionScanMode.Incremental, "\"incremental\"")]
    [InlineData(GameDetectionScanMode.Hybrid, "\"hybrid\"")]
    public void ModesTravelAsCamelCaseStrings(GameDetectionScanMode mode, string expectedJson)
    {
        Assert.Equal(expectedJson, JsonSerializer.Serialize(mode));
        Assert.Equal(mode, JsonSerializer.Deserialize<GameDetectionScanMode>(expectedJson));
    }

    // The two modes that name a single run resolve to it whatever the clock says, so a stale or
    // missing timestamp can never turn a chosen full scan into an incremental one.
    [Theory]
    [InlineData(GameDetectionScanMode.Full, false)]
    [InlineData(GameDetectionScanMode.Incremental, true)]
    public void ANamedModeIgnoresTheClock(GameDetectionScanMode mode, bool expectIncremental)
    {
        Assert.Equal(expectIncremental, mode.IsIncremental(null, Now));
        Assert.Equal(expectIncremental, mode.IsIncremental(Now.AddDays(-30), Now));
        Assert.Equal(expectIncremental, mode.IsIncremental(Now, Now));
    }

    [Theory]
    [InlineData(-8, false)] // a week overdue: the run is the full one
    [InlineData(-7, false)] // exactly a week old: the run is the full one
    [InlineData(-6, true)]  // inside the week: the run stays incremental
    [InlineData(0, true)]   // the full scan just finished
    public void HybridRunsFullOnlyOnceTheLastFullScanIsAWeekOld(int daysSinceFullScan, bool expectIncremental)
    {
        Assert.Equal(
            expectIncremental,
            GameDetectionScanMode.Hybrid.IsIncremental(Now.AddDays(daysSinceFullScan), Now));
    }

    // Nothing recorded means no baseline for the incremental days to build on, so the first hybrid
    // run is the full one that creates it.
    [Fact]
    public void HybridRunsFullWhenNoFullScanHasBeenRecorded()
    {
        Assert.False(GameDetectionScanMode.Hybrid.IsIncremental(lastFullScanUtc: null, nowUtc: Now));
    }

    // A clock corrected backwards leaves a stamp dated in the future. Reading that as an overdue week
    // would start a full scan on every tick until the clock caught up, so the run stays incremental
    // and the week resumes on its own once the clock passes the stamp.
    [Fact]
    public void HybridStaysIncrementalWhenTheStampIsInTheFuture()
    {
        Assert.True(GameDetectionScanMode.Hybrid.IsIncremental(Now.AddDays(3), Now));
    }

    // The week has to advance rather than stick: the full run re-anchors it, the ticks after it stay
    // incremental and leave the stamp alone, and a week on the full scan comes round again.
    [Fact]
    public void AFullHybridRunReanchorsTheWeekAndLeavesTheStampAloneUntilTheNextOne()
    {
        var state = StateTestMethods.CreateStateService(_root);
        state.SetGameDetectionScanMode(GameDetectionScanMode.Hybrid);

        Assert.False(GameDetectionScanMode.Hybrid.IsIncremental(state.GetGameDetectionLastFullScan(), Now));
        state.SetGameDetectionLastFullScan(Now);

        Assert.True(GameDetectionScanMode.Hybrid.IsIncremental(state.GetGameDetectionLastFullScan(), Now.AddHours(1)));
        Assert.True(GameDetectionScanMode.Hybrid.IsIncremental(state.GetGameDetectionLastFullScan(), Now.AddDays(6)));
        Assert.Equal(Now, state.GetGameDetectionLastFullScan());

        Assert.False(GameDetectionScanMode.Hybrid.IsIncremental(state.GetGameDetectionLastFullScan(), Now.AddDays(7)));
    }

    // A restart mid-week must not move which day the full scan lands on. This is why the week is
    // anchored on a stored timestamp: a tally of runs would have to be rebuilt after a restart and
    // after a tick the container slept through, while the stamp answers the same question either way.
    [Fact]
    public void ARestartMidWeekDoesNotMoveTheDayTheFullScanLandsOn()
    {
        var beforeRestart = StateTestMethods.CreateStateService(_root);
        beforeRestart.SetGameDetectionScanMode(GameDetectionScanMode.Hybrid);
        beforeRestart.SetGameDetectionLastFullScan(Now);

        var afterRestart = StateTestMethods.CreateStateService(_root);

        Assert.Equal(GameDetectionScanMode.Hybrid, afterRestart.GetGameDetectionScanMode());
        Assert.Equal(Now, afterRestart.GetGameDetectionLastFullScan());
        Assert.True(afterRestart.GetGameDetectionScanMode()
            .IsIncremental(afterRestart.GetGameDetectionLastFullScan(), Now.AddDays(4)));
        Assert.False(afterRestart.GetGameDetectionScanMode()
            .IsIncremental(afterRestart.GetGameDetectionLastFullScan(), Now.AddDays(7)));
    }

    [Fact]
    public void TheRegistryStoresAModeForGameDetection()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var registry = CreateRegistry(state);

        Assert.True(registry.SetScanMode("gameDetection", GameDetectionScanMode.Hybrid));
        Assert.Equal(GameDetectionScanMode.Hybrid, state.GetGameDetectionScanMode());
    }

    // Route keys arrive from the URL, so the key check has to read the way every other key lookup in
    // the registry reads rather than being case-sensitive on its own.
    [Fact]
    public void TheRegistryAcceptsTheKeyInAnyCasing()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var registry = CreateRegistry(state);

        Assert.True(registry.SetScanMode("GAMEDETECTION", GameDetectionScanMode.Incremental));
        Assert.Equal(GameDetectionScanMode.Incremental, state.GetGameDetectionScanMode());
    }

    [Fact]
    public void TheRegistryRefusesAScheduleThatHasNoScanModeAndStoresNothing()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var registry = CreateRegistry(state);
        registry.SetScanMode("gameDetection", GameDetectionScanMode.Hybrid);

        Assert.False(registry.SetScanMode("depotMapping", GameDetectionScanMode.Incremental));
        Assert.Equal(GameDetectionScanMode.Hybrid, state.GetGameDetectionScanMode());
    }

    // The scan mode is a single value rather than a per-service entry, so the per-service loops in
    // ResetToDefaults never reach it. Left behind, a schedule the user had switched to incremental
    // would go on running incrementally after a reset that restored everything else.
    [Fact]
    public void ResetToDefaultsReturnsTheModeToFull()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var registry = CreateRegistry(state);
        registry.SetScanMode("gameDetection", GameDetectionScanMode.Incremental);

        registry.ResetToDefaults();

        Assert.Equal(GameDetectionScanMode.Full, state.GetGameDetectionScanMode());
    }

    private static ServiceScheduleRegistry CreateRegistry(IStateService stateService)
    {
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        return new ServiceScheduleRegistry(Array.Empty<IHostedService>(), stateService, notifications, tracker);
    }
}
