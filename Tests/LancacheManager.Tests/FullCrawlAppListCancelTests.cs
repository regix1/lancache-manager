using System.Net;
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
/// A full depot crawl asks the Steam Web API whether it can list every app, then downloads the app list
/// (V2 in one request, V1 with a key in pages, or V1 after V2 sent an empty list). A cancel during any of
/// these steps ends the listing at once, as a cancel, instead of waiting for the download to finish or
/// time out and then ending the card red.
/// </summary>
public sealed class FullCrawlAppListCancelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ACancelDuringTheFullCrawlsAppListing_EndsTheListingAsACancel(bool useV1, bool cancelDuringTheDownload)
    {
        var step = cancelDuringTheDownload ? "app-list download" : "availability check";
        await CancelTheListingAsync(
            new AppListSteamApi(useV1, cancelDuringTheDownload), withApiKey: useV1, $"{(useV1 ? "V1" : "V2")} {step}");
    }

    [Fact]
    public async Task ACancelDuringTheV1FallbackAfterAnEmptyV2List_EndsTheListingAsACancel()
    {
        await CancelTheListingAsync(
            new AppListSteamApi(useV1: false, holdTheDownload: true, emptyV2List: true), withApiKey: true,
            "V1 fallback app-list download (after V2 sent an empty list)");
    }

    private static async Task CancelTheListingAsync(AppListSteamApi steamApi, bool withApiKey, string step)
    {
        var root = Path.Combine(Path.GetTempPath(), "full-crawl-app-list-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, tracker, steamApi, withApiKey);
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
            Assert.True(ended,
                $"The full crawl was still waiting on the Steam Web API {step} {Timeout.TotalSeconds} s after its run was canceled");
            var error = await Record.ExceptionAsync(() => listing);
            Assert.True(error is OperationCanceledException,
                $"A cancel during the Steam Web API {step} ended the listing with \"{error}\", not a cancel");
        }
        finally
        {
            steamApi.Answer.TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (listing is not null) await Task.WhenAny(listing, Task.Delay(Timeout));
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static SteamKit2Service NewService(string root, SteamService steam, IUnifiedOperationTracker tracker, AppListSteamApi steamApi, bool withApiKey)
    {
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
            new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths),
            NullLogger<SecureStateEncryptionService>.Instance);
        var storage = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
        if (withApiKey) storage.UpdateAuthData(auth => auth.SteamApiKey = "steam-api-key", saveSavedLogin: false);
        var state = new StateService(NullLogger<StateService>.Instance, paths, encryption, storage);
        state.SetSetupCompleted(true);
        var scopes = DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
        var clients = new SingleHandlerHttpClients(steamApi);
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

    /// <summary>
    /// The Steam Web API: V2 answers its availability check, or (for V1) refuses so the key's V1 check runs.
    /// With <c>emptyV2List</c>, V2 passes its check, sends an empty app list, then refuses the check that
    /// follows, so the listing falls back to V1. One step, a check or the app-list download of the version in
    /// use, is held until the test answers it or the request itself is canceled, as a real request is.
    /// </summary>
    private sealed class AppListSteamApi(bool useV1, bool holdTheDownload, bool emptyV2List = false) : HttpMessageHandler
    {
        private int _v2Requests;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            bool check;
            string body;
            if (url.Contains("/ISteamApps/GetAppList/v2/", StringComparison.Ordinal))
            {
                if (useV1) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var number = Interlocked.Increment(ref _v2Requests);
                body = """{"applist":{"apps":[]}}""";
                if (emptyV2List)
                {
                    // The check, then the download (an empty list), then the check after it refused.
                    return number <= 2
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                        : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                // The check and the download use the same V2 address; the check comes first.
                check = number == 1;
            }
            else if (url.Contains("/IStoreService/GetAppList/v1/", StringComparison.Ordinal))
            {
                check = !url.Contains("max_results=50000", StringComparison.Ordinal);
                body = """{"response":{"apps":[]}}""";
            }
            else
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            if (check != holdTheDownload)
            {
                Entered.TrySetResult();
                return await Answer.Task.WaitAsync(cancellationToken);
            }

            return check
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }
}
