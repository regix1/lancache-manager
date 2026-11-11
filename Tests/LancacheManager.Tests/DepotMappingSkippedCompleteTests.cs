using System.Reflection;
using System.Text.Json;
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
/// The browser's full-scan prompt ignores a depot mapping complete event whose status is
/// "skipped" and closes the prompt on any other successful one. A scheduled run that stops
/// because Steam requires a full scan must therefore say "skipped" on the wire.
/// </summary>
public sealed class DepotMappingSkippedCompleteTests
{
    // The same serializer options the hub uses (Program.cs AddJsonProtocol).
    private static readonly JsonSerializerOptions HubJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task AScheduledDepotRunSkippedForAFullScanSendsSkippedOnItsCompleteEvent()
    {
        var root = Path.Combine(Path.GetTempPath(), "depot-skipped-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var sent = (RecordingNotifications)(object)notifications;
        using var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
        var service = NewService(root, steam, notifications, tracker);
        try
        {
            service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));

            await (Task)typeof(SteamKit2Service)
                .GetMethod("ReportRunSkippedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, ["signalr.depotMapping.scan.skippedFullRequired", CancellationToken.None])!;

            var body = Assert.Single(sent.Events, item => item.EventName == "DepotMappingComplete").Payload;
            Assert.NotNull(body);
            var complete = JsonSerializer.SerializeToElement(body, body.GetType(), HubJsonOptions);
            Assert.Equal(
                "signalr.depotMapping.scan.skippedFullRequired",
                complete.GetProperty("stageKey").GetString());
            Assert.Equal("skipped", complete.GetProperty("status").GetString());
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private static SteamKit2Service NewService(
        string root,
        SteamService steam,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker tracker)
    {
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
            new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths),
            NullLogger<SecureStateEncryptionService>.Instance);
        var storage = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
        var state = new StateService(NullLogger<StateService>.Instance, paths, encryption, storage);
        var scopes = DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
        var clients = DispatchProxy.Create<IHttpClientFactory, NullReturningProxy>();
        return new SteamKit2Service(
            NullLogger<SteamKit2Service>.Instance,
            scopes,
            steam,
            new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state),
            state,
            clients,
            notifications,
            new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage),
            storage,
            tracker,
            null!);
    }
}
