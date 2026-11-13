using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The shared state for Hybrid schedule ticks: what a GitHub import leaves on disk and in the saved state,
/// Hybrid mode, no Steam Web API, and a faked Steam answer on whether a full scan is required.
/// </summary>
public sealed class SteamKit2HybridFullScanFallbackTests
{
    private const uint ImportedChangeNumber = 40843620;

    /// <summary>The state a GitHub import leaves, in Hybrid mode, with Steam answering whether it needs a
    /// full scan (the requiresFullScan argument of CreateAsync). Used by
    /// SteamKit2HybridKeepsGitHubImportSeparateTests, and by SteamKit2HybridFullAppUpdateTests with
    /// Steam still knowing the change number (false).</summary>
    internal sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _container;
        private readonly ProcessManager _processes;
        private readonly SteamService _steam;

        private Harness(string root, ServiceProvider container, ProcessManager processes, SteamService steam,
            SteamAnswersFullScanRequired service, RecordingNotifications sent)
        {
            _root = root;
            _container = container;
            _processes = processes;
            _steam = steam;
            Service = service;
            Sent = sent;
        }

        public SteamAnswersFullScanRequired Service { get; }
        public RecordingNotifications Sent { get; }

        public static async Task<Harness> CreateAsync(IOperationWaitlist queue, bool requiresFullScan = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "steam-hybrid-fallback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            Directory.CreateDirectory(paths.GetPicsDirectory());

            var services = new ServiceCollection();
            var databaseName = "steam-hybrid-fallback-" + Guid.NewGuid().ToString("N");
            services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddSingleton(queue);
            var container = services.BuildServiceProvider();
            var scopes = container.GetRequiredService<IServiceScopeFactory>();
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.SteamDepotMappings.Add(new SteamDepotMapping
                {
                    DepotId = 10,
                    AppId = 2,
                    AppName = "Owner",
                    IsOwner = true,
                    Source = "github"
                });
                await db.SaveChangesAsync();
            }

            var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
            var steam = new SteamService(new HttpClient(), NullLogger<SteamService>.Instance);
            var encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
                new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths),
                NullLogger<SecureStateEncryptionService>.Instance);
            var storage = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
            var state = new StateService(NullLogger<StateService>.Instance, paths, encryption, storage);
            // No HTTP client: the Steam Web API answers nothing (and no key is stored), and a GitHub download fails.
            var clients = DispatchProxy.Create<IHttpClientFactory, NullReturningProxy>();
            var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state);

            pics.WritePicsJsonFile("""
                {"metadata":{"lastChangeNumber":40843620,"version":"2.8","lastUpdated":"2026-11-11T18:05:16Z"},
                 "depotMappings":{"10":{"ownerId":2,"appIds":[2],"appNames":["Owner"],"discoveredAt":"2026-11-11T18:05:16Z"}}}
                """);
            state.UpdateState(saved =>
            {
                saved.SetupCompleted = true;
                saved.LastPicsChangeNumber = ImportedChangeNumber;
                saved.LastFullPicsCrawl = DateTime.UtcNow.AddDays(-1);
            });

            var service = new SteamAnswersFullScanRequired(
                scopes, steam, pics, state, clients, notifications,
                new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage),
                storage, tracker, requiresFullScan);
            SetField(service, "_initialized", true);
            SetField(service, "_isRunning", true);
            SetField(service, "_crawlIncrementalMode", "hybrid");
            service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));
            return new Harness(root, container, processes, steam, service, (RecordingNotifications)(object)notifications);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            _steam.Dispose();
            _processes.Dispose();
            await _container.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void SetField(object instance, string name, object value)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                field.SetValue(instance, value);
                return;
            }
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
    }

    /// <summary>Steam answers the check 9,053 changes later; with requiresFullScan it no longer knows the
    /// saved change number and asks for a full scan.</summary>
    internal sealed class SteamAnswersFullScanRequired(
        IServiceScopeFactory scopes,
        SteamService steam,
        PicsDataService pics,
        StateService state,
        IHttpClientFactory clients,
        ISignalRNotificationService notifications,
        SteamWebApiService webApi,
        SteamAuthStorageService storage,
        IUnifiedOperationTracker tracker,
        bool requiresFullScan)
        : SteamKit2Service(
            NullLogger<SteamKit2Service>.Instance, scopes, steam, pics, state, clients, notifications, webApi,
            storage, tracker, null!)
    {
        public Task RunScheduledTickAsync(CancellationToken stoppingToken) => ExecuteWorkAsync(stoppingToken);

        internal override Task<(uint CurrentChangeNumber, bool RequiresFullScan)> CheckChangesAsync(
            uint changeNumberToCheck, CancellationToken ct)
            => Task.FromResult((changeNumberToCheck + 9053, requiresFullScan));
    }

    /// <summary>Another job holds the queue, so every depot mapping request waits behind it.</summary>
    internal sealed class ParkingWaitlist : IOperationWaitlist
    {
        public List<OperationRequestKey> DepotMappingRequests { get; } = [];

        public Task<OperationStartResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            OperationRequestKey requestKey,
            bool reportRefusal = false,
            RunNotice? notice = null,
            string? target = null)
        {
            if (type == OperationType.DepotMapping) DepotMappingRequests.Add(requestKey);
            return Task.FromResult(new OperationStartResponse { Status = OperationStartStatus.Waiting });
        }

        public Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type, ConflictScope scope, Func<Task<Guid?>> start, CancellationToken ct,
            OperationRequestKey requestKey)
            => Task.FromResult<OperationConflictResponse?>(new OperationConflictResponse { StageKey = "errors.conflict.duplicate" });
    }
}
