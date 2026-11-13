using System.Net;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A depot Apply looks each download's app up in the Steam store, and the store service lets 5 lookups
/// run at once. With all 5 taken by lookups Steam has not answered, the Apply waits for a free one. A user who force
/// stops that Apply must see it end and free its run, so a depot run waiting on its card starts, without waiting for
/// the 5 lookups to be answered.
/// </summary>
public sealed class ForceStoppedApplyStoreLookupTests
{
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AForceStoppedApplyWaitingForAStoreLookup_EndsAndTheWaitingDepotRunStarts()
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        // The store holds every lookup; the lookups pass it no cancel, so only an answer ends them.
        var store = new HeldHandler();
        using var lookups = new SteamService(new HttpClient(store), NullLogger<SteamService>.Instance);
        typeof(SteamKit2Service).GetField("_steamService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(host.Service, lookups);
        var slots = Assert.IsType<SemaphoreSlim>(typeof(SteamService)
            .GetField("_apiSemaphore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lookups));
        try
        {
            // A download of depot 10, which the mappings table gives to app 2, so the Apply looks app 2 up.
            var database = Assert.IsType<TestDatabase>(CacheScanGateHarness.GetField(host, "_database"));
            await using (var db = database.Factory.CreateDbContext())
            {
                db.Downloads.Add(new Download
                {
                    Service = "steam", ClientIp = "127.0.0.1", Datasource = "Default", DepotId = 10,
                    StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // Five other lookups take every slot and wait for Steam's answer.
            _ = Enumerable.Range(1001, 5).Select(appId => lookups.GetGameInfoAsync(appId)).ToList();
            var deadline = DateTime.UtcNow + ForceStoppedCrawlHost.Timeout;
            while (slots.CurrentCount > 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "The five held lookups never took every store lookup slot");
                await Task.Delay(20);
            }

            var apply = host.Service.ManuallyApplyDepotMappingsAsync();
            await WaitForAWaiterAsync(slots, "The depot Apply never waited for a store lookup slot");
            var stopped = host.RunningCrawlId;

            Assert.Equal(ForceStoppedCrawlHost.CrawlWaits, await host.AskForFullCrawlAsync());
            await host.ForceStopAsync(stopped);

            var ended = await Task.WhenAny(apply, Task.Delay(StopLimit)) == apply;
            Assert.True(ended,
                $"The force-stopped depot Apply was still running {StopLimit.TotalSeconds:0} s after the stop: it waits " +
                "for a store lookup slot that the stop does not reach, so its run is not freed until a held lookup is answered");
            await host.CrawlFromTheWaitingCardAsync(1);
        }
        finally
        {
            store.Answer.TrySetResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>
    /// After an Apply saves the names it found, it asks for a banner pass through the operation
    /// queue, which waits for the queue's lock. With another request holding that lock in its start,
    /// a force-stopped Apply waiting there must end and free its run while the lock is still held; the depot run
    /// waiting on its card starts once the lock is free.
    /// </summary>
    [Fact]
    public async Task AForceStoppedApplyWaitingForTheQueueToAskForBanners_EndsAndTheWaitingDepotRunStarts()
    {
        await using var host = await ForceStoppedCrawlHost.CreateAsync();
        var hostServices = Assert.IsType<ServiceProvider>(CacheScanGateHarness.GetField(host, "_container"));
        var queue = hostServices.GetRequiredService<IOperationWaitlist>();
        var tracker = Assert.IsType<UnifiedOperationTracker>(CacheScanGateHarness.GetField(host, "_tracker"));
        // The Apply reads and saves through its scope factory, so each scope gets its own context on the test database,
        // and the banner pass it asks for comes from the same container.
        await using var services = new ServiceCollection()
            .AddScoped(_ => hostServices.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>())
            .AddSingleton(queue)
            .AddSingleton(hostServices.GetRequiredService<IOperationConflictChecker>())
            .AddSingleton(built => new GameImageFetchService(
                built, NullLogger<GameImageFetchService>.Instance, new ConfigurationBuilder().Build(),
                DispatchProxy.Create<IStateService, NullReturningProxy>(),
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                DispatchProxy.Create<IImageCacheService, NullReturningProxy>(), tracker))
            .BuildServiceProvider();
        CacheScanGateHarness.SetField(host.Service, "_scopeFactory", services.GetRequiredService<IServiceScopeFactory>());
        var gateHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? held = null;
        try
        {
            // A download of depot 10, which the mappings table gives to app 2, so the Apply names it and saves.
            var database = Assert.IsType<TestDatabase>(CacheScanGateHarness.GetField(host, "_database"));
            await using (var db = database.Factory.CreateDbContext())
            {
                db.Downloads.Add(new Download
                {
                    Service = "steam", ClientIp = "127.0.0.1", Datasource = "Default", DepotId = 10,
                    StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
            }

            // The Apply is held in its read of the mappings table while a full crawl is asked for.
            host.ReadGate.Arm();
            var apply = host.Service.ManuallyApplyDepotMappingsAsync();
            await host.ReadGate.Entered.Task.WaitAsync(ForceStoppedCrawlHost.Timeout);
            var stopped = host.RunningCrawlId;
            Assert.Equal(ForceStoppedCrawlHost.CrawlWaits, await host.AskForFullCrawlAsync());

            // Another request takes the queue's lock and keeps it while its start runs.
            var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            held = queue.EnqueueAsync(
                OperationType.StatusCheck, ConflictScope.Bulk(), "Status Check",
                async () =>
                {
                    holding.TrySetResult();
                    await gateHolder.Task;
                    var id = tracker.RegisterOperation(OperationType.StatusCheck, "Status Check", new CancellationTokenSource());
                    tracker.CompleteOperation(id, success: true);
                    return id;
                },
                CancellationToken.None, OperationRequestKey.Plain);
            await holding.Task.WaitAsync(ForceStoppedCrawlHost.Timeout);

            host.ReadGate.Open();
            await WaitForAWaiterAsync(Assert.IsType<SemaphoreSlim>(CacheScanGateHarness.GetField(queue, "_gate")),
                "The depot Apply never waited for the operation queue's lock to ask for banners");
            await host.ForceStopAsync(stopped);

            var ended = await Task.WhenAny(apply, Task.Delay(StopLimit)) == apply;
            Assert.True(ended,
                $"The force-stopped depot Apply was still running {StopLimit.TotalSeconds:0} s after the stop: it waits " +
                "for the operation queue's lock to ask for a banner pass, which the stop does not reach, so its run is " +
                "not freed until the request holding that lock is done");

            gateHolder.TrySetResult();
            await host.CrawlFromTheWaitingCardAsync(1);
        }
        finally
        {
            gateHolder.TrySetResult();
            host.ReadGate.Open();
            if (held is not null) await Task.WhenAny(held, Task.Delay(ForceStoppedCrawlHost.Timeout));
        }
    }

    /// <summary>Waits until a task waits on <paramref name="semaphore"/>.</summary>
    private static async Task WaitForAWaiterAsync(SemaphoreSlim semaphore, string never)
    {
        var waiters = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow + ForceStoppedCrawlHost.Timeout;
        while (waiters.GetValue(semaphore) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, never);
            await Task.Delay(20);
        }
    }
}
