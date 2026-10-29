using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // While the daemon connection is down the run gets no progress through no fault of its own; the cancel the
    // sweep would store is sent at the reconnect and stops a download that may still be running.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExpirySweep_RecoveringRun_IsNotFailedAsStalled(bool runRecovering, bool sessionRecovering)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var run = await fixture.StartAsync("10");
        var now = DateTime.UtcNow;
        run.LastProgressTicksUtc = now.AddMinutes(-10).Ticks;
        run.Recovering = runRecovering;
        fixture.Session.Recovering = sessionRecovering;
        fixture.Session.NextRecoveryAtUtc = now.AddHours(1);

        var result = await fixture.Daemon.ProcessSessionExpiryAsync(now).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.StalledFailed);
        Assert.False(run.CancelRequested);
        Assert.Null(run.CancelReason);
        Assert.Empty(fixture.Client.Cancelled);
        var row = Assert.Single(await fixture.History.GetRunsAsync(fixture.Session.Id, CancellationToken.None), saved => saved.Id == run.PrefillRunId);
        Assert.False(row.CancelRequested);
    }

    // The schedule's watch stops its run after the schedule's stall timeout; the sweep must not stop it sooner.
    [Fact]
    public async Task ExpirySweep_ScheduledRun_UsesTheScheduleStallTimeout()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var config = Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true);
        Assert.True(config.StallTimeout > TimeSpan.FromMinutes(10));
        typeof(PrefillDaemonServiceBase).GetField("_stateService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Daemon, OrchestrationStateService.New(config, 90));
        var scheduledRun = await fixture.StartAsync("10", Guid.NewGuid());
        var manualRun = await fixture.StartAsync("20");
        var now = DateTime.UtcNow;
        scheduledRun.LastProgressTicksUtc = now.AddMinutes(-10).Ticks;
        manualRun.LastProgressTicksUtc = now.AddMinutes(-10).Ticks;

        Assert.Equal(1, (await fixture.Daemon.ProcessSessionExpiryAsync(now).WaitAsync(TimeSpan.FromSeconds(10))).StalledFailed);
        Assert.True(manualRun.CancelRequested);
        Assert.False(scheduledRun.CancelRequested);

        Assert.Equal(1, (await fixture.Daemon.ProcessSessionExpiryAsync(now.AddMinutes(-10) + config.StallTimeout + TimeSpan.FromSeconds(1))
            .WaitAsync(TimeSpan.FromSeconds(10))).StalledFailed);
        Assert.True(scheduledRun.CancelRequested);
        Assert.Equal("stalled", scheduledRun.CancelReason);
    }
}
