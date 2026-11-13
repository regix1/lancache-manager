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
/// The browser's depot progress poll treats an answer whose phase is "completed" as a finished run,
/// whatever the busy flag says (PicsProgressContext). So while a GitHub import runs after a crawl that
/// ended completed, the progress answer has to name a phase of its own, or the page reads the running
/// import as not processing.
/// </summary>
public sealed class GitHubImportProgressPhaseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AGitHubImportAfterACompletedCrawl_ReadsAsProcessingToThePoll()
    {
        var root = Path.Combine(Path.GetTempPath(), "github-import-progress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // GitHub taking the depot map download and answering only when the test says so.
        var gitHub = new HeldHandler();
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, tracker, gitHub);
        Task<bool>? import = null;
        try
        {
            // The previous crawl ended completed.
            CacheScanGateHarness.SetField(service, "_currentStatus", DepotScanPhase.Completed);

            import = service.ImportFromGitHubAsync(CancellationToken.None, RunTrigger.Manual);
            await gitHub.Entered.Task.WaitAsync(Timeout);

            var progress = service.GetProgress();
            Assert.True(progress.IsProcessing && progress.Status != DepotScanPhase.Completed,
                $"While the GitHub import downloads, the progress answer reads isProcessing={progress.IsProcessing}, "
                + $"status={progress.Status}, which the progress poll shows as not processing");
        }
        finally
        {
            gitHub.Answer.TrySetResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
            if (import is not null) await Task.WhenAny(import, Task.Delay(Timeout));
            service.Dispose();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static SteamKit2Service NewService(string root, SteamService steam, IUnifiedOperationTracker tracker, HeldHandler gitHub)
    {
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
            new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths),
            NullLogger<SecureStateEncryptionService>.Instance);
        var storage = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
        var state = new StateService(NullLogger<StateService>.Instance, paths, encryption, storage);
        var scopes = new ServiceCollection().AddSingleton<IOperationConflictChecker>(OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance)).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var clients = CacheScanGateHarness.CreateProxy<IHttpClientFactory>((_, _) => new HttpClient(gitHub, disposeHandler: false));
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
