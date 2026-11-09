using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// A terminal save whose commit landed but whose answer was lost is retried, and the retry answers for the lost
/// commit. The games it returns are published as cached again, so it returns only the games that commit ended.
/// </summary>
public sealed class PrefillRetriedRunSaveTests
{
    [Fact]
    public async Task ARetriedTerminalSaveDoesNotPublishGamesAnEarlierSaveEndedAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var started = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10", "20"]);
        await fixture.RefreshAsync();
        var run = Assert.IsType<DaemonRun>(fixture.Daemon.GetRun(fixture.Session.Id, started.RunId!.Value));
        var now = DateTimeOffset.UtcNow;
        var first = new DaemonRunItem { AppId = "10", State = "completed", Result = "success", TotalBytes = 100, Sequence = 2 };
        var second = new DaemonRunItem { AppId = "20", State = "completed", Result = "success", TotalBytes = 100, Sequence = 3 };
        var progress = run.Snapshot with { State = "downloading", Sequence = 2, UpdatedAt = now };
        var end = run.Snapshot with { State = "completed", Sequence = 3, UpdatedAt = now.AddSeconds(1) };
        Assert.True((await fixture.History.SaveRunAsync(run, progress, [first], false, CancellationToken.None)).Applied);
        var saved = await fixture.History.SaveRunAsync(run, end, [first, second], true, CancellationToken.None);
        Assert.Equal(["20"], saved.Items.Select(item => item.AppId));

        var retried = await fixture.History.SaveRunAsync(run, end, [first, second], true, CancellationToken.None);

        Assert.True(retried.Applied);
        Assert.Equal(["20"], retried.Items.Select(item => item.AppId));
    }
}
