using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A full depot crawl first asks the Steam Web API whether it can list every app. A cancel while that
/// question is open ends the crawl at once, as a cancel, instead of waiting for the request to time out
/// and then reporting the Web API as unavailable.
/// </summary>
public sealed class FullCrawlWebApiProbeCancelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ACancelDuringTheFullCrawlsWebApiCheck_EndsTheListingAsACancel()
    {
        var root = Path.Combine(Path.GetTempPath(), "full-crawl-web-api-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // A Steam Web API that takes every request and answers only when the test says so.
        var steamApi = new HeldHandler();
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, tracker, steamApi);
        using var run = new CancellationTokenSource();
        Task? listing = null;
        try
        {
            CacheScanGateHarness.SetField(service, "_steamApps", new SteamKit2.SteamClient().GetHandler<SteamKit2.SteamApps>()!);
            listing = (Task)typeof(SteamKit2Service)
                .GetMethod("GetAppIdsViaPicsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [run.Token, false])!;
            await steamApi.Entered.Task.WaitAsync(Timeout);

            run.Cancel();

            var ended = await Task.WhenAny(listing, Task.Delay(Timeout)) == listing;
            Assert.True(ended, $"The full crawl was still waiting on the Steam Web API check {Timeout.TotalSeconds} s after its run was canceled");
            var error = await Record.ExceptionAsync(() => listing);
            Assert.True(error is OperationCanceledException,
                $"A cancel during the Steam Web API check ended the listing with \"{error}\", not a cancel");
        }
        finally
        {
            steamApi.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            if (listing is not null) await Task.WhenAny(listing, Task.Delay(Timeout));
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

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
        var scopes = DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
        var clients = CacheScanGateHarness.CreateProxy<IHttpClientFactory>((_, _) => new HttpClient(steamApi, disposeHandler: false));
        return new SteamKit2Service(
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
    }
}
