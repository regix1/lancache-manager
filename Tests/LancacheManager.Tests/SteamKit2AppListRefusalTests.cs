using System.Reflection;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SteamKit2;
using SteamKit2.Internal;

namespace LancacheManager.Tests;

/// <summary>
/// After a GitHub import the saved change number is the snapshot's, and Steam answers a changes request
/// from it with RequiresFullUpdate false and RequiresFullAppUpdate true: it still knows the number but
/// will not list that many changed apps. A Hybrid tick then runs the incremental crawl, which finds the
/// changed apps from each known app's own change number, and commits Steam's current number.
/// Steam is played by a listener on the client's outgoing messages: each request is read from its bytes
/// and answered through <see cref="SteamApps.HandleMsg"/>, so SteamKit2 parses the answers as it parses
/// Steam's, and the client never opens a connection.
/// </summary>
public sealed class SteamKit2AppListRefusalTests
{
    private const uint ImportedChangeNumber = 40843620;
    private const uint CurrentChangeNumber = 40852673;

    [Fact]
    public async Task AHybridRunThatSteamRefusesTheAppListCrawlsTheKnownAppsChangedSinceTheMarkerAsync()
    {
        await using var harness = await Harness.CreateAsync();

        await harness.Service.RunScheduledTickAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        var steam = harness.Steam;
        Assert.Empty(steam.Unanswered);
        Assert.Equal([[2u, 3u, 4u]], steam.ChangeNumberRequests);
        Assert.Equal([[2u]], steam.AppRequests);

        var state = harness.State.GetState();
        Assert.Equal(CurrentChangeNumber, state.LastPicsChangeNumber);
        Assert.Equal(harness.LastFullCrawl, state.LastFullPicsCrawl);

        await using (var db = harness.Database.Factory.CreateDbContext())
        {
            var added = await db.SteamDepotMappings.AsNoTracking().SingleAsync(row => row.DepotId == 11);
            Assert.Equal(2, added.AppId);
            Assert.True(added.IsOwner);
        }

        Assert.Null(harness.Service.PendingFullScan);
        Assert.DoesNotContain(harness.Sent.Events, item => item.EventName == SignalREvents.AutomaticScanSkipped);
        var completed = Assert.Single(harness.Sent.Events, item => item.EventName == "DepotMappingComplete");
        Assert.Contains("signalr.depotMapping.finalized",
            JsonSerializer.Serialize(completed.Payload, completed.Payload!.GetType()), StringComparison.Ordinal);
        Assert.Contains(harness.Log.Entries, entry => entry.Message.Contains(
            $"Steam would not list the apps changed from #{ImportedChangeNumber} to #{CurrentChangeNumber}; 1 of 3 known apps changed",
            StringComparison.Ordinal));

        // Nothing left the machine: no Steam connection was started and no HTTP request was sent.
        Assert.Same(harness.Client, Field(harness.Service, "_steamClient"));
        Assert.Null(Field(harness.Client, "connection"));
        Assert.Null(Field(harness.Client, "connectionSetupTask"));
        Assert.DoesNotContain(harness.Log.Entries, entry => entry.Message.Contains("Connecting to Steam", StringComparison.Ordinal));
        Assert.Empty(harness.HttpRequests);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _container;
        private readonly ProcessManager _processes;
        private readonly SteamService _steamStore;

        private Harness(string root, TestDatabase database, ServiceProvider container, ProcessManager processes,
            SteamService steamStore, StateService state, ScheduledTickService service, CapturingLogger<SteamKit2Service> log,
            RecordingNotifications sent, List<Uri?> httpRequests, DateTime lastFullCrawl)
        {
            _root = root;
            LastFullCrawl = lastFullCrawl;
            Database = database;
            _container = container;
            _processes = processes;
            _steamStore = steamStore;
            State = state;
            Service = service;
            Log = log;
            Sent = sent;
            HttpRequests = httpRequests;
            Client = (SteamClient)Field(service, "_steamClient")!;
            Steam = new ScriptedSteam((SteamApps)Field(service, "_steamApps")!);
            Client.DebugNetworkListener = Steam;
        }

        public TestDatabase Database { get; }
        public StateService State { get; }
        public ScheduledTickService Service { get; }
        public CapturingLogger<SteamKit2Service> Log { get; }
        public RecordingNotifications Sent { get; }
        public List<Uri?> HttpRequests { get; }
        public SteamClient Client { get; }
        public ScriptedSteam Steam { get; }
        public DateTime LastFullCrawl { get; }

        public static async Task<Harness> CreateAsync()
        {
            var database = await TestDatabase.CreateAsync();
            var root = Path.Combine(Path.GetTempPath(), "steam-app-list-refusal-" + Guid.NewGuid().ToString("N"));
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

            var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
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
            var container = services.BuildServiceProvider();
            var scopes = container.GetRequiredService<IServiceScopeFactory>();

            var httpRequests = new List<Uri?>();
            var clients = new SingleHandlerHttpClients(new RefusingHandler(httpRequests));
            var steamStore = new SteamService(clients.CreateClient(string.Empty), NullLogger<SteamService>.Instance);
            var state = StateTestMethods.CreateStateService(root);
            var storage = (SteamAuthStorageService)Field(state, "_steamAuthStorage")!;
            var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state);
            pics.WritePicsJsonFile("""
                {"metadata":{"lastChangeNumber":40843620,"version":"2.8","lastUpdated":"2026-11-11T18:05:16Z"},
                 "depotMappings":{
                  "10":{"ownerId":2,"appIds":[2],"appNames":["Owner 2"],"discoveredAt":"2026-11-11T18:05:16Z"},
                  "20":{"ownerId":3,"appIds":[3],"appNames":["Owner 3"],"discoveredAt":"2026-11-11T18:05:16Z"},
                  "30":{"ownerId":4,"appIds":[4],"appNames":["Owner 4"],"discoveredAt":"2026-11-11T18:05:16Z"}}}
                """);
            var lastFullCrawl = DateTime.UtcNow.AddDays(-1);
            state.UpdateState(saved =>
            {
                saved.SetupCompleted = true;
                saved.LastPicsChangeNumber = ImportedChangeNumber;
                saved.LastFullPicsCrawl = lastFullCrawl;
            });

            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
            var log = new CapturingLogger<SteamKit2Service>();
            var service = new ScheduledTickService(
                log, scopes, steamStore, pics, state, clients, notifications,
                new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage),
                storage, tracker, new PrefillCacheService(database.Factory, NullLogger<PrefillCacheService>.Instance));
            SetField(service, "_initialized", true);
            SetField(service, "_isRunning", true);
            SetField(service, "_crawlIncrementalMode", "hybrid");
            service.SelectRunNotice(new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled));

            // The service builds its own Steam client; the test marks it as an anonymous session that is
            // already logged on, so the service's session check never connects.
            typeof(SteamKit2Service).GetMethod("InitializeSteamClient", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, null);
            CacheScanGateHarness.LogOnAnonymously(service);

            return new Harness(root, database, container, processes, steamStore, state, service, log,
                (RecordingNotifications)(object)notifications, httpRequests, lastFullCrawl);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            _steamStore.Dispose();
            _processes.Dispose();
            await _container.DisposeAsync();
            await Database.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Answers the client's PICS requests the way Steam answered them for the user's server: the changes
    /// since the imported number are known but too many to list, and each known app's metadata carries
    /// its own change number. Only app 2 changed after the import.
    /// </summary>
    private sealed class ScriptedSteam(SteamApps apps) : IDebugNetworkListener
    {
        private static readonly Dictionary<uint, uint> AppChangeNumbers = new()
        {
            [2] = 40850000,
            [3] = ImportedChangeNumber,
            [4] = 40800000
        };

        public List<EMsg> Unanswered { get; } = [];
        public List<uint[]> ChangeNumberRequests { get; } = [];
        public List<uint[]> AppRequests { get; } = [];

        public void OnIncomingNetworkMessage(EMsg msgType, byte[] bytes)
        {
        }

        public void OnOutgoingNetworkMessage(EMsg msgType, byte[] bytes)
        {
            var packet = new PacketClientMsgProtobuf(msgType, bytes);
            switch (msgType)
            {
                case EMsg.ClientPICSChangesSinceRequest:
                    AnswerChanges(new ClientMsgProtobuf<CMsgClientPICSChangesSinceRequest>(packet));
                    break;
                case EMsg.ClientPICSAccessTokenRequest:
                    Answer(EMsg.ClientPICSAccessTokenResponse, packet.SourceJobID, new CMsgClientPICSAccessTokenResponse());
                    break;
                case EMsg.ClientPICSProductInfoRequest:
                    AnswerApps(new ClientMsgProtobuf<CMsgClientPICSProductInfoRequest>(packet));
                    break;
                case EMsg.ClientLogOff:
                    break;
                default:
                    Unanswered.Add(msgType);
                    break;
            }
        }

        private void AnswerChanges(ClientMsgProtobuf<CMsgClientPICSChangesSinceRequest> request)
        {
            var since = request.Body.since_change_number;
            var answer = new CMsgClientPICSChangesSinceResponse
            {
                current_change_number = CurrentChangeNumber,
                since_change_number = since,
                force_full_update = false,
                force_full_app_update = since != 0
            };
            Answer(EMsg.ClientPICSChangesSinceResponse, request.SourceJobID, answer);
        }

        private void AnswerApps(ClientMsgProtobuf<CMsgClientPICSProductInfoRequest> request)
        {
            var requested = request.Body.apps.Select(app => app.appid).ToArray();
            var answer = new CMsgClientPICSProductInfoResponse
            {
                meta_data_only = request.Body.meta_data_only,
                response_pending = false
            };
            if (request.Body.meta_data_only)
            {
                ChangeNumberRequests.Add(requested);
                foreach (var appId in requested)
                {
                    answer.apps.Add(new CMsgClientPICSProductInfoResponse.AppInfo
                    {
                        appid = appId,
                        change_number = AppChangeNumbers[appId]
                    });
                }
            }
            else
            {
                AppRequests.Add(requested);
                foreach (var appId in requested)
                {
                    // Steam sends app info as NUL-terminated text; app 2 now also has depot 11.
                    answer.apps.Add(new CMsgClientPICSProductInfoResponse.AppInfo
                    {
                        appid = appId,
                        change_number = AppChangeNumbers[appId],
                        buffer = Encoding.UTF8.GetBytes(
                            $$"""
                            "appinfo" { "appid" "{{appId}}" "common" { "name" "Owner {{appId}}" "type" "game" } "depots" { "10" { } "11" { } } }
                            """ + "\0")
                    });
                }
            }
            Answer(EMsg.ClientPICSProductInfoResponse, request.SourceJobID, answer);
        }

        private void Answer<TBody>(EMsg type, JobID requestJob, TBody body) where TBody : ProtoBuf.IExtensible, new()
        {
            var response = new ClientMsgProtobuf<TBody>(type) { TargetJobID = requestJob };
            response.Body = body;
            apps.HandleMsg(new PacketClientMsgProtobuf(type, response.Serialize()));
        }
    }

    /// <summary>The scheduled tick of a service that talks to the scripted Steam above.</summary>
    private sealed class ScheduledTickService(
        CapturingLogger<SteamKit2Service> log,
        IServiceScopeFactory scopes,
        SteamService steam,
        PicsDataService pics,
        StateService state,
        IHttpClientFactory clients,
        ISignalRNotificationService notifications,
        SteamWebApiService webApi,
        SteamAuthStorageService storage,
        IUnifiedOperationTracker tracker,
        PrefillCacheService prefillCache)
        : SteamKit2Service(log, scopes, steam, pics, state, clients, notifications, webApi, storage, tracker, prefillCache)
    {
        public Task RunScheduledTickAsync(CancellationToken stoppingToken) => ExecuteWorkAsync(stoppingToken);
    }

    /// <summary>Records every HTTP request and answers it with an error, so none leaves the machine.</summary>
    private sealed class RefusingHandler(List<Uri?> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests) requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    private static object? Field(object instance, string name)
    {
        for (var type = instance.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
        }
        throw new MissingFieldException(instance.GetType().FullName, name);
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
}
