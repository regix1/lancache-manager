using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public sealed class PrefillRunRecoveryTests
{
    [Fact]
    public async Task LostAcknowledgementAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Client.LoseAcknowledgement = true;
        var run = await fixture.StartAsync("10");
        Assert.Equal(1, fixture.Client.StartCount);
        Assert.False(run.Recovering);
        Assert.False(run.Completion.Task.IsCompleted);
        Assert.Equal("downloading", run.Snapshot.State);
    }

    [Fact]
    public async Task AcceptedRunInventoryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Client.OmitInventory = true;
        var run = await fixture.StartAsync("10");
        Assert.False(run.Completion.Task.IsCompleted);
        Assert.False(run.HistoryIncomplete);
        Assert.Equal(1, fixture.Client.StartCount);
    }

    [Fact]
    public async Task SameInstanceRecoveryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var schedule = Guid.NewGuid();
        var original = await fixture.StartAsync("10", schedule);
        fixture.Client.Offline = true;
        fixture.Session.Recovering = true;
        await fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, original.PrefillRunId);
        fixture.Session.Runs.Clear();
        fixture.Client.Offline = false;
        await fixture.RefreshAsync();
        await fixture.RefreshAsync();
        var restored = Assert.IsType<DaemonRun>(fixture.Daemon.GetRun(fixture.Session.Id, original.PrefillRunId));
        Assert.Equal(original.Snapshot.StartedAt, restored.Snapshot.StartedAt);
        Assert.Equal(schedule, restored.PrefillScheduleId);
        Assert.Equal("silent", restored.NotificationMode);
        Assert.Equal("10", Assert.Single(restored.Options.AppIds!));
        Assert.True(restored.CancelRequested);
        Assert.Equal("cancelled", (await restored.Completion.Task).Snapshot.State);
        Assert.Equal(1, fixture.Client.StartCount);
    }

    [Fact]
    public async Task NewInstanceInterruptionAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var first = await fixture.StartAsync("10");
        var second = await fixture.StartAsync("20");
        fixture.Client.InstanceId = Guid.NewGuid().ToString();
        fixture.Client.Pages.Clear();
        await fixture.RefreshAsync();
        Assert.Equal("instance-changed", (await first.Completion.Task).Snapshot.Reason);
        Assert.Equal("instance-changed", (await second.Completion.Task).Snapshot.Reason);
        Assert.True(first.HistoryIncomplete);
        Assert.Equal(2, fixture.Client.StartCount);
        Assert.False(fixture.Session.IsPrefilling);
    }

    [Fact]
    public async Task MissingOutcomeAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Pages.Clear();
        await fixture.RefreshAsync();
        Assert.Equal("outcome-unknown", (await run.Completion.Task).Snapshot.Reason);
        Assert.True(run.HistoryIncomplete);
        await using var context = new AppDbContext(fixture.Options);
        Assert.Empty(await context.PrefillCachedApps.ToListAsync());
    }

    [Fact]
    public async Task DisconnectedAdmissionAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Offline = true;
        await fixture.RefreshAsync();
        Assert.True(fixture.Session.Recovering);
        Assert.False(run.Completion.Task.IsCompleted);
        await Assert.ThrowsAsync<DaemonCommandException>(() => fixture.StartAsync("20"));
        Assert.Equal(1, fixture.Client.StartCount);
    }

    [Fact]
    public async Task RetainedTerminalAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "completed", 100, "success");
        fixture.Client.MissingPages = true;
        await fixture.RefreshAsync();
        Assert.Equal("completed", (await run.Completion.Task).Snapshot.State);
        Assert.True(run.HistoryIncomplete);
        await using var context = new AppDbContext(fixture.Options);
        Assert.Empty(await context.PrefillCachedApps.ToListAsync());
    }

    [Fact]
    public async Task TerminalPagingAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var run = await fixture.StartAsync("10");
        var page = fixture.Client.Pages[run.PrefillRunId];
        var terminal = page.Operation with { State = "completed", Sequence = 3, TotalApps = 2, CompletedApps = 2 };
        var reads = new List<int>();
        fixture.Client.ReadPage = (_, offset) =>
        {
            reads.Add(offset);
            return new DaemonOperationPage(reads.Count == 1 ? page.Operation : terminal,
                page.Options, true, 2, [new DaemonRunItem { AppId = offset == 0 ? "10" : "20",
                    State = "completed", Result = "success", Sequence = 3, BytesTransferred = 50, TotalBytes = 50 }],
                offset == 0 ? 1 : null);
        };
        await fixture.RefreshAsync();
        Assert.Equal([0, 1, 0, 1], reads);
        Assert.Equal("completed", (await run.Completion.Task).Snapshot.State);
        Assert.Equal(2, run.Items.Count);
        Assert.False(run.HistoryIncomplete);
    }
}
