using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Prefill traffic separation for a persistent container the manager re-adopted after a restart.
/// </summary>
public sealed partial class PrefillContainerOrchestrationTests
{
    /// <summary>
    /// The addresses come from the re-adopt itself; a run started on that session and a run the
    /// manager only learns about from the daemon's inventory both save them, so marking finds
    /// their downloads and leaves another device alone.
    /// </summary>
    [Fact]
    public async Task AReadoptedContainersRunsAreMarkedIncludingOneAdoptedFromTheDaemon()
    {
        const string cacheAddress = "192.168.1.10";
        await using var database = await TestDatabase.CreateAsync();
        var sessionService = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);
        var sessionId = await SeedActivePersistentRowAsync(sessionService, expiresAt: DateTime.UtcNow.AddDays(30));
        var gateway = new RecordingContainerGateway();
        gateway.AddContainer(RunningHostNetworkContainer(sessionId, cacheAddress));
        var daemon = new TestSteamDaemon(
            MakeDeps(database.Factory, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)), gateway);
        await daemon.StartAsync(CancellationToken.None);
        var since = DateTime.UtcNow.AddMinutes(-1);

        var session = SessionsOf(daemon).Values.Single(s => s.IsPersistent);
        var client = (RunClient)(object)DispatchProxy.Create<IDaemonClient, RunClient>();
        session.Client = (IDaemonClient)(object)client;
        session.Capabilities = RunClient.Capabilities(client.InstanceId);
        session.AuthState = DaemonAuthState.Authenticated;

        var started = await daemon.PrefillAsync(session.Id, appIds: ["10"], notificationMode: "silent");
        var adoptedId = Guid.NewGuid();
        var options = new DaemonRunOptions { AppIds = ["20"] };
        client.Pages[adoptedId] = new DaemonOperationPage(
            new DaemonRunSnapshot
            {
                OperationId = adoptedId.ToString(),
                DaemonInstanceId = client.InstanceId,
                Sequence = 1,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                State = "downloading",
                SelectionResolved = true,
                TotalApps = 1
            },
            options, true, 1, [new DaemonRunItem { AppId = "20", Sequence = 1 }], null);
        await session.RecoveryWork.WaitAsync();
        session.RecoveryWork.Release();
        await daemon.RefreshRunsAsync(session.Id);

        await using (var seed = new AppDbContext(database.Options))
        {
            var now = DateTime.UtcNow;
            seed.Downloads.AddRange(
                new Download { Service = "steam", ClientIp = cacheAddress, StartTimeUtc = now, EndTimeUtc = now.AddMinutes(1), CacheMissBytes = 100 },
                new Download { Service = "steam", ClientIp = "10.0.0.1", StartTimeUtc = now, EndTimeUtc = now.AddMinutes(1), CacheMissBytes = 100 });
            await seed.SaveChangesAsync();
        }
        await sessionService.MarkPrefillRowsAsync(since, "default", DateTime.UtcNow, CancellationToken.None);

        await using var read = new AppDbContext(database.Options);
        var runs = await read.PrefillRuns.AsNoTracking()
            .Where(run => run.SessionId == session.Id)
            .ToDictionaryAsync(run => run.Id);
        Assert.Equal("recovered", runs[adoptedId].Source);
        foreach (var id in new[] { started.RunId!.Value, adoptedId })
        {
            Assert.Equal([cacheAddress], JsonSerializer.Deserialize<List<string>>(runs[id].ClientAddressesJson!));
        }
        var marked = await read.Downloads.AsNoTracking()
            .Where(d => d.IsPrefill)
            .Select(d => d.ClientIp)
            .ToListAsync();
        Assert.Equal([cacheAddress], marked);

        daemon.Dispose();
    }
}
