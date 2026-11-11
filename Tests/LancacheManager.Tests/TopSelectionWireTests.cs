using System.Net.Sockets;
using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// A Top prefill must reach every daemon that offers Top. Steam reads the count from `top`; the Xbox and
/// Epic daemons read `selection=top` and the count from `topCount`, and treat `top` as true/false.
/// </summary>
public sealed class TopSelectionWireTests
{
    [Fact]
    public async Task TopPrefillNamesTheSelectionAndTheCountForEveryDaemonAsync()
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
                TopCount = 50,
                MaxConcurrency = 1
            }, cancellationToken: timeout.Token)).Success);
        }
        finally
        {
            release.TrySetResult();
        }

        var command = await daemon;
        Assert.Equal("prefill", command.GetProperty("type").GetString());
        var parameters = command.GetProperty("parameters");
        Assert.True(parameters.TryGetProperty("top", out var steamCount) && steamCount.GetString() == "50",
            "The Steam daemon no longer receives its count in `top`.");
        Assert.True(parameters.TryGetProperty("selection", out var selection) && selection.GetString() == "top",
            "The prefill command has no `selection=top`, so the Xbox and Epic daemons read an empty explicit "
            + "selection and refuse the Top run (\"Explicit selection is empty\").");
        Assert.True(parameters.TryGetProperty("topCount", out var count) && count.GetString() == "50",
            "The prefill command has no `topCount=50`, so the Xbox and Epic daemons never see the requested count.");
    }
}
