using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A full depot crawl whose app listing fails (the Steam Web API answers with an error) leaves the
/// depot mappings the table holds in use: the progress answer the Schedules page reads still counts them.
/// </summary>
public sealed class FailedFullCrawlKeepsMappingsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AFullCrawlThatCannotListApps_KeepsTheStoredMappingsInUse()
    {
        await using var database = await TestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "failed-full-crawl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        Directory.CreateDirectory(paths.GetPicsDirectory());

        await using (var db = database.Factory.CreateDbContext())
        {
            foreach (var (depotId, appId) in new[] { (10L, 2L), (20L, 3L), (30L, 4L) })
            {
                db.SteamDepotMappings.Add(new SteamDepotMapping
                {
                    DepotId = depotId, AppId = appId, AppName = "Owner " + appId, IsOwner = true, Source = "github"
                });
            }
            await db.SaveChangesAsync();
        }

        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            checker,
            NullLogger<OperationWaitlistService>.Instance);
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(database.Options));
        services.AddTransient<IOperationWaitlist>(_ => queue);
        services.AddSingleton<IOperationConflictChecker>(checker);
        await using var container = services.BuildServiceProvider();
        var scopes = container.GetRequiredService<IServiceScopeFactory>();

        var clients = new SingleHandlerHttpClients(new UnavailableSteamWebApi());
        using var steamStore = new SteamService(clients.CreateClient(string.Empty), NullLogger<SteamService>.Instance);
        var state = StateTestMethods.CreateStateService(root);
        var storage = (SteamAuthStorageService)CacheScanGateHarness.GetField(state, "_steamAuthStorage")!;
        var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state);
        state.UpdateState(saved => saved.SetupCompleted = true);
        var service = new SteamKit2Service(
            NullLogger<SteamKit2Service>.Instance, scopes, steamStore, pics, state, clients,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage),
            storage, tracker, new PrefillCacheService(database.Factory, NullLogger<PrefillCacheService>.Instance));
        try
        {
            CacheScanGateHarness.SetField(service, "_initialized", true);
            CacheScanGateHarness.SetField(service, "_isRunning", true);
            // An anonymous session that is already logged on, so the crawl never connects to Steam.
            typeof(SteamKit2Service).GetMethod("InitializeSteamClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, null);
            CacheScanGateHarness.LogOnAnonymously(service);
            // The mappings the service loads from the table when it starts.
            await (Task)typeof(SteamKit2Service)
                .GetMethod("LoadDepotMappingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [false, CancellationToken.None])!;
            Assert.Equal(3, service.GetProgress().DepotMappingsFound);

            Assert.True(await service.StartRebuildThroughQueueAsync(
                false, new RunNotice(NotificationMode.All, RunTrigger.Manual), CancellationToken.None),
                "The full crawl did not start");
            if (CacheScanGateHarness.GetField(service, "_currentBuildTask") is Task crawl) await crawl.WaitAsync(Timeout);

            var progress = service.GetProgress();
            Assert.True(progress.DepotMappingsFound == 3,
                $"After the failed full crawl the progress answer counts {progress.DepotMappingsFound} depot mappings; the table holds 3");
        }
        finally
        {
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // The Steam Web API answering every request with an error, so the full crawl cannot list apps.
    private sealed class UnavailableSteamWebApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
}
