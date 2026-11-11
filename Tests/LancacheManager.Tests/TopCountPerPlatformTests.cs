using System.Net.Sockets;
using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// A Top count above 100 differs by platform: the Epic daemon takes any positive count, and the Xbox daemon
/// refuses one above 100. So a Top 150 must reach the daemon as 150, never cut to 100 on the way, and a saved
/// Xbox Top schedule must not hold a count its daemon will refuse, while a saved Epic one keeps its 150.
/// </summary>
public sealed class TopCountPerPlatformTests
{
    private const int AskedCount = 150;

    [Fact]
    public async Task TopPrefillSendsTheCountTheUserAskedForAsync()
    {
        using var endpoint = DaemonClientConnectionLifecycleTests.LoopbackEndpoint.Create(true);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var runId = Guid.NewGuid();
        var instanceId = Guid.NewGuid().ToString();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var daemon = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var status = await DaemonClientConnectionLifecycleTests.ReadRequestWithBodyAsync(stream, timeout.Token);
            await DaemonClientConnectionLifecycleTests.WriteResponseAsync(stream, status.GetProperty("id").GetString()!,
                true, null, null, timeout.Token,
                result: JsonSerializer.SerializeToElement(RunClient.Capabilities(instanceId), JsonSerializerOptions.Web));
            var prefill = await DaemonClientConnectionLifecycleTests.ReadRequestWithBodyAsync(stream, timeout.Token);
            await DaemonClientConnectionLifecycleTests.WriteResponseAsync(stream, prefill.GetProperty("id").GetString()!,
                true, null, null, timeout.Token, result: JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    runId,
                    daemonInstanceId = instanceId,
                    state = "started"
                }));
            await release.Task.WaitAsync(timeout.Token);
            return prefill;
        }, timeout.Token);
        try
        {
            Assert.True((await client.GetStatusAsync(timeout.Token))?.SupportsConcurrentPrefill);
            Assert.True((await client.PrefillAsync(runId, instanceId, new DaemonRunOptions
            {
                Selection = "top",
                TopCount = AskedCount,
                MaxConcurrency = 1
            }, cancellationToken: timeout.Token)).Success);
        }
        finally
        {
            release.TrySetResult();
        }

        var parameters = (await daemon).GetProperty("parameters");
        Assert.True(parameters.TryGetProperty("topCount", out var count),
            "The Top prefill command has no `topCount`, so the Epic and Xbox daemons never see the count.");
        Assert.True(count.GetString() == AskedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"A Top {AskedCount} prefill reached the daemon as topCount={count.GetString()}: Epic runs that many "
            + "games and reports success, and Xbox runs them instead of refusing the count it does not support.");
    }

    [Fact]
    public void SavedTopScheduleHoldsOnlyACountItsPlatformRuns()
    {
        var xbox = SaveTopSchedule(PrefillPlatform.Xbox);
        Assert.True(xbox.Refused || xbox.SavedCount <= 100,
            $"An Xbox Top {AskedCount} schedule was saved with count {xbox.SavedCount}; the Xbox daemon refuses a "
            + "count above 100, so the editor shows a count no run of this schedule will do.");

        var epic = SaveTopSchedule(PrefillPlatform.Epic);
        Assert.False(epic.Refused, $"An Epic Top {AskedCount} schedule was refused, but the Epic daemon runs any count.");
        Assert.Equal(AskedCount, epic.SavedCount);
    }

    /// <summary>
    /// Saves the default config with <paramref name="platform"/>'s schedule set to Top <see cref="AskedCount"/>,
    /// through the same validation every schedule save goes through.
    /// </summary>
    private static (bool Refused, int? SavedCount) SaveTopSchedule(PrefillPlatform platform)
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        ScheduledPrefillServiceConfigDto WithTop(ScheduledPrefillServiceConfigDto service) => service.ServiceId != platform
            ? service
            : ScheduledPrefillConfigFactory.CopyServiceWithSchedules(service, service.Schedules
                .Select(schedule => new ScheduledPrefillSchedule
                {
                    Id = schedule.Id,
                    Name = schedule.Name,
                    Enabled = true,
                    IntervalHours = schedule.IntervalHours,
                    CustomSchedule = schedule.CustomSchedule,
                    Preset = ScheduledPrefillPreset.Top,
                    TopCount = AskedCount,
                    SelectedAppIds = [.. schedule.SelectedAppIds],
                    OperatingSystems = [.. schedule.OperatingSystems],
                    Force = schedule.Force,
                    MaxConcurrency = schedule.MaxConcurrency,
                    NotificationMode = schedule.NotificationMode,
                    NotificationDisplayMode = schedule.NotificationDisplayMode
                })
                .ToList());
        var edited = new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = WithTop(config.Steam),
            Epic = WithTop(config.Epic),
            Xbox = WithTop(config.Xbox),
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };

        ScheduledPrefillConfigDto saved;
        try
        {
            saved = ScheduledPrefillConfigFactory.Validate(edited);
        }
        catch (ScheduledPrefillConfigValidationException)
        {
            return (true, null);
        }

        var service = platform == PrefillPlatform.Xbox ? saved.Xbox : saved.Epic;
        var schedule = Assert.Single(service.Schedules);
        Assert.Equal(ScheduledPrefillPreset.Top, schedule.Preset);
        return (false, schedule.TopCount);
    }
}
