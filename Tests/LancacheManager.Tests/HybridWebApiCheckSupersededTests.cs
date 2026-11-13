using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A Hybrid depot mapping tick that needs a full crawl asks the Steam Web API whether it can list every
/// app. A GitHub import that finishes while that question is open has already given the person new
/// mappings, so the tick does not go on to start a full crawl nobody asked for. An import that finishes
/// after the tick has started its crawl changes nothing about that crawl.
/// </summary>
public sealed class HybridWebApiCheckSupersededTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnImportFinishingDuringTheHybridWebApiCheck_StopsTheTicksFullCrawl(bool importFinishesDuringTheCheck)
    {
        var root = Path.Combine(Path.GetTempPath(), "hybrid-web-api-superseded-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var steamApi = new HeldHandler();
        var requested = new ConcurrentQueue<OperationRequestKey>();
        var queue = CacheScanGateHarness.CreateProxy<IOperationWaitlist>((method, args) =>
        {
            if (method.Name == nameof(IOperationWaitlist.StartUnlessBusyAsync)) requested.Enqueue((OperationRequestKey)args![4]!);
            return Task.FromResult<OperationConflictResponse?>(null);
        });
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, tracker, steamApi, queue);
        Task? tick = null;
        try
        {
            service.SelectRunNotice(new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
            tick = (Task)typeof(SteamKit2Service)
                .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [CancellationToken.None])!;
            await steamApi.Entered.Task.WaitAsync(Timeout);

            // The import's own end, as ImportFromGitHubAsync reports it.
            if (importFinishesDuringTheCheck) service.UpdateLastCrawlTime(4242);
            steamApi.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"applist\":{\"apps\":[]}}")
            });
            await tick.WaitAsync(Timeout);
            if (!importFinishesDuringTheCheck) service.UpdateLastCrawlTime(4242);

            var fullCrawls = requested.Count(key => key == DepotMappingRequestKey.Crawl(incrementalOnly: false));
            if (importFinishesDuringTheCheck)
            {
                Assert.True(fullCrawls == 0,
                    $"The tick asked for {fullCrawls} full crawl(s) after an import finished during its Steam Web API check");
            }
            else
            {
                Assert.True(fullCrawls == 1, $"The tick asked for {fullCrawls} full crawl(s) with nothing finishing during its check");
            }
        }
        finally
        {
            steamApi.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            if (tick is not null) await Task.WhenAny(tick, Task.Delay(Timeout));
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ARebuildStartingDuringTheHybridWebApiCheck_StopsTheTicksFullCrawl()
    {
        var root = Path.Combine(Path.GetTempPath(), "hybrid-web-api-rebuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var steamApi = new HeldHandler();
        var requested = new ConcurrentQueue<OperationRequestKey>();
        var queue = CacheScanGateHarness.CreateProxy<IOperationWaitlist>((method, args) =>
        {
            if (method.Name == nameof(IOperationWaitlist.StartUnlessBusyAsync)) requested.Enqueue((OperationRequestKey)args![4]!);
            return Task.FromResult<OperationConflictResponse?>(null);
        });
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, tracker, steamApi, queue);
        Task? tick = null;
        try
        {
            service.SelectRunNotice(new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
            tick = (Task)typeof(SteamKit2Service)
                .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [CancellationToken.None])!;
            await steamApi.Entered.Task.WaitAsync(Timeout);

            // A GitHub import or full scan holds the rebuild flag while it runs, as ImportFromGitHubAsync does.
            CacheScanGateHarness.SetField(service, "_rebuildActive", 1);
            steamApi.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"applist\":{\"apps\":[]}}")
            });
            await tick.WaitAsync(Timeout);

            var fullCrawls = requested.Count(key => key == DepotMappingRequestKey.Crawl(incrementalOnly: false));
            Assert.True(fullCrawls == 0,
                $"The tick asked for {fullCrawls} full crawl(s) while a rebuild started during its Steam Web API check was running");
        }
        finally
        {
            steamApi.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            if (tick is not null) await Task.WhenAny(tick, Task.Delay(Timeout));
            CacheScanGateHarness.SetField(service, "_rebuildActive", 0);
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // A Hybrid service whose last full crawl is a day old and that has no depot baseline, so its tick
    // resolves to incremental, the viability check asks for a full scan without calling Steam, and the
    // tick asks the Steam Web API whether Hybrid may run that full crawl. Its scopes hand out only the
    // operation queue; the database lookups find nothing.
    private static SteamKit2Service NewService(
        string root, SteamService steam, IUnifiedOperationTracker tracker, HeldHandler steamApi, IOperationWaitlist queue)
    {
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
            new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths),
            NullLogger<SecureStateEncryptionService>.Instance);
        var storage = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
        var state = new StateService(NullLogger<StateService>.Instance, paths, encryption, storage);
        state.SetSetupCompleted(true);
        state.UpdateState(saved => saved.LastFullPicsCrawl = DateTime.UtcNow.AddDays(-1));
        var services = CacheScanGateHarness.CreateProxy<IServiceProvider>((_, args) =>
            (Type)args![0]! == typeof(IOperationWaitlist) ? queue : null);
        var scope = CacheScanGateHarness.CreateProxy<IServiceScope>((method, _) =>
            method.Name == "get_ServiceProvider" ? services : null);
        var scopes = CacheScanGateHarness.CreateProxy<IServiceScopeFactory>((_, _) => scope);
        var clients = CacheScanGateHarness.CreateProxy<IHttpClientFactory>((_, _) => new HttpClient(steamApi, disposeHandler: false));
        var service = new SteamKit2Service(
            NullLogger<SteamKit2Service>.Instance,
            scopes,
            steam,
            new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state),
            state,
            clients,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage),
            storage,
            tracker,
            null!);
        service.CrawlIncrementalMode = "hybrid";
        CacheScanGateHarness.SetField(service, "_initialized", true);
        CacheScanGateHarness.SetField(service, "_isRunning", true);
        return service;
    }
}
