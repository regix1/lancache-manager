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
/// app. A cancel or a host shutdown while that question is open ends the tick at once instead of
/// waiting for the request to time out.
/// </summary>
public sealed class HybridWebApiCheckCancelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ACancelDuringTheHybridWebApiCheck_EndsTheTickAtOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "hybrid-web-api-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // A Steam Web API that takes every request and answers only when the test says so.
        var steamApi = new HeldHandler();
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, tracker, steamApi);
        using var run = new CancellationTokenSource();
        Task? tick = null;
        try
        {
            service.SelectRunNotice(new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
            tick = (Task)typeof(SteamKit2Service)
                .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [run.Token])!;
            await steamApi.Entered.Task.WaitAsync(Timeout);

            run.Cancel();

            var ended = await Task.WhenAny(tick, Task.Delay(Timeout)) == tick;
            Assert.True(ended, $"The tick was still waiting on the Steam Web API check {Timeout.TotalSeconds} s after its run was canceled");
        }
        finally
        {
            steamApi.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            if (tick is not null) await Task.WhenAny(tick, Task.Delay(Timeout));
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // A Hybrid service whose last full crawl is a day old and that has no depot baseline, so its tick
    // resolves to incremental, the viability check asks for a full scan without calling Steam, and the
    // tick goes on to ask the Steam Web API whether Hybrid may run that full crawl.
    private static SteamKit2Service NewService(string root, SteamService steam, IUnifiedOperationTracker tracker, HeldHandler steamApi)
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
        var scopes = DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
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
