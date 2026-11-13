using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using SteamKit2;

namespace LancacheManager.Tests;

/// <summary>
/// A Steam depot service whose table holds 3 mappings, signed on to Steam anonymously, with the real
/// force stop beside it. Each full crawl's Steam Web API app-list download is held until the test answers
/// it, the read of the whole mappings table and the count of its rows can each be held once, and every
/// log-off sent to Steam is recorded. Shared by the tests of a full crawl that is force stopped while
/// another run starts.
/// </summary>
internal sealed class ForceStoppedCrawlHost : IAsyncDisposable
{
    public const int TableMappings = 3;

    /// <summary>The change number of the depot file <see cref="WriteDepotFile"/> writes.</summary>
    public const uint FileChangeNumber = 100;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TestDatabase _database;
    private readonly string _root;
    private readonly string _picsDirectory;
    private readonly ProcessManager _processes;
    private readonly UnifiedOperationTracker _tracker;
    private readonly ServiceProvider _container;
    private readonly SteamService _steamStore;
    private readonly OperationCancellationService _cancellation;
    private readonly List<Task> _crawls = [];
    private TaskCompletionSource _noCrawlShown = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ForceStoppedCrawlHost(
        TestDatabase database, string root, string picsDirectory, ProcessManager processes, UnifiedOperationTracker tracker,
        ServiceProvider container, SteamService steamStore, SteamKit2Service service, OperationCancellationService cancellation,
        HeldAppListApi steamApi,
        MappingsReadGate readGate, CountReadGate countGate)
    {
        _database = database;
        _root = root;
        _picsDirectory = picsDirectory;
        _processes = processes;
        _tracker = tracker;
        _container = container;
        _steamStore = steamStore;
        Service = service;
        _cancellation = cancellation;
        SteamApi = steamApi;
        ReadGate = readGate;
        CountGate = countGate;
        ConfigurableScheduledService.ServiceExecutionStateChanged += OnExecutionStateChanged;
    }

    public SteamKit2Service Service { get; }

    public HeldAppListApi SteamApi { get; }

    public MappingsReadGate ReadGate { get; }

    public CountReadGate CountGate { get; }

    public SteamLogOffs LogOffs { get; } = new();

    /// <summary>Set when the service next shows no crawl running (the depot status dot goes off).</summary>
    public Task NoCrawlShown => _noCrawlShown.Task;

    public static async Task<ForceStoppedCrawlHost> CreateAsync()
    {
        var database = await TestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), "force-stopped-crawl-host-" + Guid.NewGuid().ToString("N"));
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

        var readGate = new MappingsReadGate();
        var countGate = new CountReadGate();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(readGate, countGate).Options;
        var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationWaitlistService(
            tracker,
            checker,
            NullLogger<OperationWaitlistService>.Instance);
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(options));
        services.AddTransient<IOperationWaitlist>(_ => queue);
        services.AddSingleton<IOperationConflictChecker>(checker);
        var container = services.BuildServiceProvider();
        var scopes = container.GetRequiredService<IServiceScopeFactory>();

        var steamApi = new HeldAppListApi();
        var clients = new SingleHandlerHttpClients(steamApi);
        var steamStore = new SteamService(clients.CreateClient(string.Empty), NullLogger<SteamService>.Instance);
        var state = StateTestMethods.CreateStateService(root);
        var storage = (SteamAuthStorageService)CacheScanGateHarness.GetField(state, "_steamAuthStorage")!;
        var pics = new PicsDataService(NullLogger<PicsDataService>.Instance, scopes, paths, state);
        state.UpdateState(saved => saved.SetupCompleted = true);
        var service = new SteamKit2Service(
            NullLogger<SteamKit2Service>.Instance, scopes, steamStore, pics, state, clients,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new SteamWebApiService(NullLogger<SteamWebApiService>.Instance, clients, storage),
            storage, tracker, new PrefillCacheService(database.Factory, NullLogger<PrefillCacheService>.Instance));
        var cancellation = new OperationCancellationService(
            tracker,
            processes,
            new OperationStateService(
                NullLogger<OperationStateService>.Instance, new ConfigurationBuilder().Build(), state, scopes,
                DispatchProxy.Create<IHostApplicationLifetime, NullReturningProxy>(), processes, tracker),
            NullLogger<OperationCancellationService>.Instance);

        var host = new ForceStoppedCrawlHost(
            database, root, paths.GetPicsDirectory(), processes, tracker, container, steamStore, service, cancellation,
            steamApi, readGate, countGate);
        CacheScanGateHarness.SetField(service, "_initialized", true);
        CacheScanGateHarness.SetField(service, "_isRunning", true);
        typeof(SteamKit2Service).GetMethod("InitializeSteamClient", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, null);
        ((SteamClient)CacheScanGateHarness.GetField(service, "_steamClient")!).DebugNetworkListener = host.LogOffs;
        CacheScanGateHarness.LogOnAnonymously(service);
        // Every parameter after the first two keeps its default, whatever the method's version declares.
        var load = typeof(SteamKit2Service).GetMethod("LoadDepotMappingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)load.Invoke(
            service,
            [false, CancellationToken.None, .. load.GetParameters().Skip(2).Select(parameter => parameter.DefaultValue)])!;
        Assert.Equal(TableMappings, service.GetProgress().DepotMappingsFound);
        return host;
    }

    /// <summary>Asks for a full crawl the way the depot scan button does. Returns the crawl's task when one
    /// started, or null when the service answered that it did not start one.</summary>
    public async Task<Task?> StartFullCrawlAsync()
    {
        if (!await Service.StartRebuildThroughQueueAsync(
                false, new RunNotice(NotificationMode.All, RunTrigger.Manual), CancellationToken.None))
        {
            return null;
        }

        var crawl = Assert.IsAssignableFrom<Task>(CacheScanGateHarness.GetField(Service, "_currentBuildTask"));
        _crawls.Add(crawl);
        return crawl;
    }

    /// <summary>What a user sees after asking for a full crawl the way the depot scan button does.</summary>
    public const string CrawlStarted = "started";
    public const string CrawlWaits = "waiting on a card";
    public const string CrawlRefusedSilently = "not started, with no waiting card";

    /// <summary>Asks for a full crawl through the queue and answers <see cref="CrawlStarted"/>,
    /// <see cref="CrawlWaits"/> or <see cref="CrawlRefusedSilently"/>.</summary>
    public async Task<string> AskForFullCrawlAsync()
    {
        if (await StartFullCrawlAsync() is not null) return CrawlStarted;
        return _tracker.GetWaitingOperations().Any(operation => operation.Type == OperationType.DepotMapping)
            ? CrawlWaits
            : CrawlRefusedSilently;
    }

    /// <summary>The crawl a waiting card turned into, once that crawl reached the app-list download of the
    /// <paramref name="crawl"/>-th full crawl.</summary>
    public async Task<Task> CrawlFromTheWaitingCardAsync(int crawl)
    {
        await SteamApi.Download(crawl).Entered.Task.WaitAsync(Timeout);
        var started = Assert.IsAssignableFrom<Task>(CacheScanGateHarness.GetField(Service, "_currentBuildTask"));
        _crawls.Add(started);
        return started;
    }

    public Guid RunningCrawlId => Assert.IsType<Guid>(CacheScanGateHarness.GetField(Service, "_currentPicsOperationId"));

    /// <summary>The force stop a user asks for from the operation card. <see cref="NoCrawlShown"/> is set
    /// again by the next time the service shows no crawl running after it.</summary>
    public async Task ForceStopAsync(Guid operationId)
    {
        _noCrawlShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(await _cancellation.ForceKillAsync(operationId), "The force stop was refused");
    }

    /// <summary>Writes the depot file a crawl saves and the local-file import reads: two depots the table does
    /// not hold, 40 (app 5) and 50 (app 6), at change number <see cref="FileChangeNumber"/>.</summary>
    public void WriteDepotFile()
        => File.WriteAllText(Path.Combine(_picsDirectory, "pics_depot_mappings.json"), DepotFile());

    /// <summary>The depot file <see cref="WriteDepotFile"/> writes, which the GitHub import can also download
    /// (<see cref="HeldAppListApi.PublishDepotFile"/>).</summary>
    public static string DepotFile()
    {
        var discovered = DateTime.UtcNow;
        var file = new PicsJsonData
        {
            Metadata = new PicsMetadata { LastUpdated = discovered, TotalMappings = 2, LastChangeNumber = FileChangeNumber },
            DepotMappings = new Dictionary<string, PicsDepotMapping>
            {
                ["40"] = new() { OwnerId = 5, AppIds = [5], AppNames = ["Owner 5"], DiscoveredAt = discovered },
                ["50"] = new() { OwnerId = 6, AppIds = [6], AppNames = ["Owner 6"], DiscoveredAt = discovered },
            },
        };
        return JsonSerializer.Serialize(file, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>How many depots the mappings table holds, the number the progress answer counts once the
    /// maps equal the table.</summary>
    public async Task<int> TableDepotCountAsync()
    {
        await using var db = _database.Factory.CreateDbContext();
        return await db.SteamDepotMappings.Select(mapping => mapping.DepotId).Distinct().CountAsync();
    }

    public async ValueTask DisposeAsync()
    {
        ConfigurableScheduledService.ServiceExecutionStateChanged -= OnExecutionStateChanged;
        ReadGate.Open();
        CountGate.Open();
        SteamApi.AnswerAll();
        foreach (var crawl in _crawls)
        {
            await Task.WhenAny(crawl, Task.Delay(Timeout));
        }

        Service.Dispose();
        _steamStore.Dispose();
        await _container.DisposeAsync();
        _processes.Dispose();
        await _database.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void OnExecutionStateChanged(string _)
    {
        if (!Service.IsRebuildRunning) _noCrawlShown.TrySetResult();
    }

    /// <summary>One app-list download the test holds and then answers.</summary>
    public sealed class HeldDownload
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Steam answers the download with an error, so the crawl cannot list apps.</summary>
        public void Fail() => Answer.TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    /// <summary>
    /// The Steam Web API V2. Each crawl first checks availability (answered at once) and then downloads the
    /// app list (held). A held download answers only when the test says so, even after a cancel: it stands
    /// for a step that has not yet seen the force stop.
    /// </summary>
    public sealed class HeldAppListApi : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<int, HeldDownload> _downloads = new();
        private int _requests;
        private volatile bool _publishDepotFile;

        /// <summary>The app-list download of the <paramref name="crawl"/>-th full crawl (1 is the first).</summary>
        public HeldDownload Download(int crawl) => _downloads.GetOrAdd(crawl, _ => new HeldDownload());

        /// <summary>From now on, a GitHub request for the depot file is answered with
        /// <see cref="DepotFile"/>, so a GitHub import downloads and writes it.</summary>
        public void PublishDepotFile() => _publishDepotFile = true;

        public void AnswerAll()
        {
            foreach (var download in _downloads.Values) download.Fail();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_publishDepotFile
                && request.RequestUri!.ToString().Contains("pics_depot_mappings.json", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(DepotFile()) };
            }

            if (!request.RequestUri!.ToString().Contains("/ISteamApps/GetAppList/v2/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var number = Interlocked.Increment(ref _requests);
            if (number % 2 == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"applist":{"apps":[]}}""") };
            }

            var download = Download(number / 2);
            download.Entered.TrySetResult();
            return await download.Answer.Task;
        }
    }

    /// <summary>Holds the first read of the whole mappings table after <see cref="Arm"/> until
    /// <see cref="Open"/>. Counts, filtered reads and writes pass. After <see cref="ArmRowRead"/> it holds
    /// only a read of the table's rows, so a check that the table has any row passes, and
    /// <see cref="FailTheRead"/> makes the held read fail instead of returning.</summary>
    public sealed class MappingsReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private volatile bool _rowsOnly;
        private volatile bool _fail;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void ArmRowRead()
        {
            _rowsOnly = true;
            Arm();
        }

        public void Open()
        {
            Interlocked.Exchange(ref _armed, 0);
            _open.TrySetResult();
        }

        /// <summary>Lets the held read go on as a database error.</summary>
        public void FailTheRead()
        {
            _fail = true;
            Open();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData commandEvent, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var text = command.CommandText;
            if (text.Contains("FROM \"SteamDepotMappings\"", StringComparison.Ordinal)
                && !text.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("WHERE", StringComparison.OrdinalIgnoreCase)
                && !(_rowsOnly && text.Contains("EXISTS", StringComparison.OrdinalIgnoreCase))
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Entered.TrySetResult();
                await _open.Task;
                if (_fail)
                {
                    throw new InvalidOperationException("The database dropped the read of the depot mappings");
                }
            }

            return result;
        }
    }

    /// <summary>Holds the first count of the mappings table's rows after <see cref="Arm"/> until
    /// <see cref="Open"/>. Every other command passes.</summary>
    public sealed class CountReadGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void Open()
        {
            Interlocked.Exchange(ref _armed, 0);
            _open.TrySetResult();
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData commandEvent, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var text = command.CommandText;
            if (text.Contains("FROM \"SteamDepotMappings\"", StringComparison.Ordinal)
                && text.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                Entered.TrySetResult();
                await _open.Task;
            }

            return result;
        }
    }

    /// <summary>Every log-off the service's Steam client sends. Steam ends the session it is sent on.</summary>
    public sealed class SteamLogOffs : IDebugNetworkListener
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void OnIncomingNetworkMessage(EMsg msgType, byte[] message)
        {
        }

        public void OnOutgoingNetworkMessage(EMsg msgType, byte[] message)
        {
            if (msgType == EMsg.ClientLogOff) Interlocked.Increment(ref _count);
        }
    }
}
