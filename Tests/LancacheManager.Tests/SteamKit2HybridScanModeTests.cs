using System.Text.Json;
using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Tests;

/// <summary>
/// Pins what a scheduled run on the hybrid mode actually does, which is a different question from
/// what the dropdown stores. IsIncrementalMode answered "anything that is not github is
/// incremental", so a hybrid value saved, persisted, survived a restart and showed the right label
/// while every scheduled run crawled incrementally, with nothing failing and nothing logged. These
/// assert the run the mode resolves to, never the value that was stored.
/// </summary>
public sealed class SteamKit2HybridScanModeTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root;

    public SteamKit2HybridScanModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-hybrid-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // The mode written to state.json comes back as a JsonElement rather than the string that was
    // written, and that is the form the old fall-through swallowed. A stored hybrid mode with a full
    // crawl a month behind it must resolve to a full run, not to the incremental one it used to.
    [Fact]
    public void StoredHybridModeResolvesToAFullRunWhenTheWeekIsUp()
    {
        var writer = StateTestMethods.CreateStateService(_root);
        writer.SetCrawlIncrementalMode("hybrid");
        writer.SetLastFullPicsCrawl(Now.AddDays(-30));

        var reader = StateTestMethods.CreateStateService(_root);
        var storedMode = reader.GetCrawlIncrementalMode();

        Assert.IsType<JsonElement>(storedMode);
        Assert.False(SteamKit2Service.IsIncrementalMode(storedMode, reader.GetLastFullPicsCrawl(), Now));
    }

    [Theory]
    [InlineData(-8, false)] // a week overdue: the run is the full one
    [InlineData(-7, false)] // exactly a week old: the run is the full one
    [InlineData(-6, true)]  // inside the week: the run stays incremental
    [InlineData(0, true)]   // the full crawl just finished
    public void HybridRunsFullOnlyOnceTheLastFullCrawlIsAWeekOld(int daysSinceFullCrawl, bool expectIncremental)
    {
        Assert.Equal(
            expectIncremental,
            SteamKit2Service.IsIncrementalMode("hybrid", Now.AddDays(daysSinceFullCrawl), Now));
    }

    // Nothing recorded means no baseline for the incremental days to build on, so the first hybrid
    // run is the full one that creates it.
    [Fact]
    public void HybridRunsFullWhenNoFullCrawlHasBeenRecorded()
    {
        Assert.False(SteamKit2Service.IsIncrementalMode("hybrid", lastFullCrawlUtc: null, nowUtc: Now));
    }

    // A clock corrected backwards leaves a stamp dated in the future. Reading that as an overdue week
    // would start a full crawl on every tick until the clock caught up, so the run stays incremental
    // and the week resumes on its own once the clock passes the stamp.
    [Fact]
    public void HybridStaysIncrementalWhenTheStampIsInTheFuture()
    {
        Assert.True(SteamKit2Service.IsIncrementalMode("hybrid", Now.AddDays(3), Now));
    }

    // The week has to advance rather than stick: the full run re-anchors it, the ticks after it stay
    // incremental and leave the stamp alone, and a week on the full crawl comes round again.
    // Stamping mirrors ExecuteWorkAsync, which calls SaveLastFullCrawlTime only on the branch where
    // incrementalOnly was false.
    [Fact]
    public void AFullHybridRunReanchorsTheWeekAndLeavesTheStampAloneUntilTheNextOne()
    {
        var state = StateTestMethods.CreateStateService(_root);
        state.SetCrawlIncrementalMode("hybrid");

        Assert.False(SteamKit2Service.IsIncrementalMode("hybrid", state.GetLastFullPicsCrawl(), Now));
        state.SetLastFullPicsCrawl(Now);

        Assert.True(SteamKit2Service.IsIncrementalMode("hybrid", state.GetLastFullPicsCrawl(), Now.AddHours(1)));
        Assert.True(SteamKit2Service.IsIncrementalMode("hybrid", state.GetLastFullPicsCrawl(), Now.AddDays(6)));
        Assert.Equal(Now, state.GetLastFullPicsCrawl());

        Assert.False(SteamKit2Service.IsIncrementalMode("hybrid", state.GetLastFullPicsCrawl(), Now.AddDays(7)));
    }

    // A restart mid-week must not move which day the full crawl lands on. This is why the week is
    // anchored on a stored timestamp: a tally of runs would have to be rebuilt after a restart and
    // after a tick the container slept through, while the stamp answers the same question either way.
    [Fact]
    public void ARestartMidWeekDoesNotMoveTheDayTheFullCrawlLandsOn()
    {
        var beforeRestart = StateTestMethods.CreateStateService(_root);
        beforeRestart.SetCrawlIncrementalMode("hybrid");
        beforeRestart.SetLastFullPicsCrawl(Now);

        Assert.True(SteamKit2Service.IsIncrementalMode(
            beforeRestart.GetCrawlIncrementalMode(), beforeRestart.GetLastFullPicsCrawl(), Now.AddDays(4)));

        // A second instance over the same directory is the restarted container reading state.json.
        var afterRestart = StateTestMethods.CreateStateService(_root);
        var storedMode = afterRestart.GetCrawlIncrementalMode();

        Assert.Equal(Now, afterRestart.GetLastFullPicsCrawl());
        Assert.True(SteamKit2Service.IsIncrementalMode(storedMode, afterRestart.GetLastFullPicsCrawl(), Now.AddDays(4)));
        Assert.False(SteamKit2Service.IsIncrementalMode(storedMode, afterRestart.GetLastFullPicsCrawl(), Now.AddDays(7)));
    }

    // The three existing modes must keep resolving exactly as they did, since the hybrid arm sits on
    // the same fall-through they rely on.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void TheStoredBooleanModesAreUnchanged(bool storedMode, bool expectIncremental)
    {
        var writer = StateTestMethods.CreateStateService(_root);
        writer.SetCrawlIncrementalMode(storedMode);

        var reader = StateTestMethods.CreateStateService(_root);

        Assert.Equal(
            expectIncremental,
            SteamKit2Service.IsIncrementalMode(reader.GetCrawlIncrementalMode(), Now.AddDays(-30), Now));
    }

    [Fact]
    public void GithubModeIsStillNotAnIncrementalRun()
    {
        var writer = StateTestMethods.CreateStateService(_root);
        writer.SetCrawlIncrementalMode("github");

        var reader = StateTestMethods.CreateStateService(_root);

        Assert.False(SteamKit2Service.IsIncrementalMode(reader.GetCrawlIncrementalMode(), null, Now));
    }
}
