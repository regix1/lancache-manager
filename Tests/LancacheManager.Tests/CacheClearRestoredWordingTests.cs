using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Tests;

/// <summary>
/// A cache clear's card is worded from keys the browser translates: the recovery poll a reload reads
/// names the stage the clear is in, and a failed clear saves the failure key and its values, so the
/// run a restart restores ends on the same sentence the live card showed.
/// </summary>
public sealed class CacheClearRestoredWordingTests : IDisposable
{
    private static readonly MethodInfo CompleteRunFromRecord = typeof(OperationStateService)
        .GetMethod("CompleteRunFromRecord", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lcm-clear-wording", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp tree the OS still holds open is not this test's concern.
        }
    }

    [Fact]
    public async Task ARestoredFailedClearEndsOnItsKeyAndTheValuesItNamesAsync()
    {
        await using var live = await ClearRun.CreateAsync(Path.Combine(_root, "live"), start: true);
        // cache_clear for a root it cannot list prints the error and exits 1.
        live.Cleaner.ExitCode = 1;

        var clearId = Assert.IsType<Guid>(await live.Clearing.StartCacheClearAsync());

        var ended = await live.WaitForTerminalAsync(clearId);
        Assert.Equal("failed", ended.Status);
        Assert.Equal("signalr.cacheClear.error.fatal", ended.Message);
        var detail = ended.FailureReason?.Context["errorDetail"]?.ToString();
        Assert.Contains("alpha could not be cleared", detail, StringComparison.Ordinal);
        var saved = live.State.LoadOperationRepairs().Single(repair => repair.Id == clearId);

        // A restart restores the run from its saved record, and the restored card ends at once from it.
        await using var restarted = await ClearRun.CreateAsync(Path.Combine(_root, "restarted"), start: false);
        await restarted.Clearing.RestoreRepairAsync(saved, CancellationToken.None);
        CompleteRunFromRecord.Invoke(restarted.Repairs.Owner, [saved]);

        var restored = Assert.Single(restarted.Repairs.Tracker.GetRuns().Runs, run => run.OperationId == clearId);
        Assert.Equal("failed", restored.Status);
        Assert.Equal("signalr.cacheClear.error.fatal", restored.Message);
        Assert.Equal("signalr.cacheClear.error.fatal", restored.FailureReason?.StageKey);
        Assert.Equal(detail, restored.FailureReason?.Context["errorDetail"]?.ToString());
        await restarted.Notifications.WhenEventAsync(SignalREvents.CacheClearingComplete).WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = Assert.Single(
            restarted.Notifications.PayloadsFor<CacheClearComplete>(SignalREvents.CacheClearingComplete));
        Assert.Equal("signalr.cacheClear.error.fatal", terminal.StageKey);
        Assert.Equal(detail, terminal.Context?["errorDetail"]?.ToString());
    }

    [Fact]
    public async Task ASuccessfulClearEndsOnAKeyLiveAndRestoredAsync()
    {
        await using var live = await ClearRun.CreateAsync(Path.Combine(_root, "live"), start: true);

        var clearId = Assert.IsType<Guid>(await live.Clearing.StartCacheClearAsync());

        Assert.Equal("completed", (await live.WaitForTerminalAsync(clearId)).Status);
        await live.Notifications.WhenEventAsync(SignalREvents.CacheClearingComplete).WaitAsync(TimeSpan.FromSeconds(5));
        var liveTerminal = Assert.Single(
            live.Notifications.PayloadsFor<CacheClearComplete>(SignalREvents.CacheClearingComplete));
        Assert.Equal("signalr.cacheClear.cleared", liveTerminal.StageKey);
        Assert.Equal(1, Convert.ToInt32(liveTerminal.Context?["directories"]));
        var saved = live.State.LoadOperationRepairs().Single(repair => repair.Id == clearId);

        // A restart restores the run from its saved record, and the restored card ends at once from it.
        await using var restarted = await ClearRun.CreateAsync(Path.Combine(_root, "restarted"), start: false);
        await restarted.Clearing.RestoreRepairAsync(saved, CancellationToken.None);
        CompleteRunFromRecord.Invoke(restarted.Repairs.Owner, [saved]);

        await restarted.Notifications.WhenEventAsync(SignalREvents.CacheClearingComplete).WaitAsync(TimeSpan.FromSeconds(5));
        var restoredTerminal = Assert.Single(
            restarted.Notifications.PayloadsFor<CacheClearComplete>(SignalREvents.CacheClearingComplete));
        Assert.True(restoredTerminal.Success);
        Assert.Equal("signalr.cacheClear.completed", restoredTerminal.StageKey);
    }

    [Fact]
    public async Task TheRecoveryPollNamesTheClearStageByKeyAsync()
    {
        await using var run = await ClearRun.CreateAsync(_root, start: true);
        // The browser reads the poll as the app serializes it: camelCase JSON.
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        JsonElement? clearing = null;
        JsonElement? ticked = null;
        run.Cleaner.OnStart = () =>
            clearing = JsonSerializer.SerializeToElement(Assert.Single(run.Clearing.GetActiveOperations()), web);
        run.Cleaner.OnTicked = () =>
            ticked = JsonSerializer.SerializeToElement(Assert.Single(run.Clearing.GetActiveOperations()), web);

        var clearId = Assert.IsType<Guid>(await run.Clearing.StartCacheClearAsync());
        Assert.Equal("completed", (await run.WaitForTerminalAsync(clearId)).Status);

        // A reload before the cleaner's first tick finds the clearing stage.
        Assert.True(clearing!.Value.TryGetProperty("stageKey", out var clearingKey), clearing.Value.GetRawText());
        Assert.Equal("signalr.cacheClear.clearing", clearingKey.GetString());
        // A reload after a tick finds the cleaner's stage with the counts its sentence names.
        Assert.True(ticked!.Value.TryGetProperty("stageKey", out var tickedKey), ticked.Value.GetRawText());
        Assert.Equal("signalr.cacheClear.progress", tickedKey.GetString());
        Assert.True(ticked.Value.TryGetProperty("context", out var context), ticked.Value.GetRawText());
        Assert.Equal(1, context.GetProperty("processed").GetInt32());
        Assert.Equal(1, context.GetProperty("totalDirs").GetInt32());
        Assert.Equal(0, context.GetProperty("activeCount").GetInt32());
    }

    private sealed class ClearRun : IAsyncDisposable
    {
        private ClearRun(
            OperationRepairTests.RepairHarness repairs,
            StateService state,
            FakeCleaner cleaner,
            CacheClearingService clearing,
            ScheduledRunReporterTests.CapturingNotificationService notifications)
        {
            Repairs = repairs;
            State = state;
            Cleaner = cleaner;
            Clearing = clearing;
            Notifications = notifications;
        }

        public OperationRepairTests.RepairHarness Repairs { get; }
        public StateService State { get; }
        public FakeCleaner Cleaner { get; }
        public CacheClearingService Clearing { get; }
        public ScheduledRunReporterTests.CapturingNotificationService Notifications { get; }

        public static async Task<ClearRun> CreateAsync(string root, bool start)
        {
            var cache = Path.Combine(root, "cache", "alpha");
            var logs = Path.Combine(root, "logs", "alpha");
            Directory.CreateDirectory(Path.Combine(cache, "aa"));
            Directory.CreateDirectory(logs);
            await File.WriteAllTextAsync(Path.Combine(cache, "aa", "0123456789abcdef0123456789abcdef"), "x");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:LogPath"] = logs,
                    ["LanCache:DataSources:0:CachePath"] = cache,
                    ["LanCache:DataSources:0:Enabled"] = "true"
                })
                .Build();
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            Directory.CreateDirectory(paths.GetOperationsDirectory());
            // The service refuses to launch a cleaner whose binary is missing.
            await File.WriteAllTextAsync(paths.GetRustCacheCleanerPath(), string.Empty);

            var state = OperationRepairTests.CreateFailingStateService(Path.Combine(root, "state"));
            // The repair a clear owes is held, so only the clear itself writes its record.
            var repairs = await OperationRepairTests.RepairHarness.CreateAsync(
                root,
                start: start,
                stateService: state,
                apply: (_, cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken));
            var datasources = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
            var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
            var cleaner = new FakeCleaner(paths);
            var clearing = new CacheClearingService(
                NullLogger<CacheClearingService>.Instance,
                notifications,
                configuration,
                paths,
                state,
                cleaner,
                datasources,
                repairs.Tracker,
                new DatasourceCapabilityService(datasources),
                repairs.Owner);
            return new ClearRun(repairs, state, cleaner, clearing, notifications);
        }

        public async Task<OperationRun> WaitForTerminalAsync(Guid operationId)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Repairs.Tracker.GetOperation(operationId)?.Status.IsTerminal() != true)
            {
                Assert.True(DateTime.UtcNow < deadline, "The clear did not end within 10 seconds");
                await Task.Delay(10);
            }
            return Assert.Single(Repairs.Tracker.GetRuns().Runs, run => run.OperationId == operationId);
        }

        public ValueTask DisposeAsync() => Repairs.DisposeAsync();
    }

    /// <summary>
    /// Plays cache_clear: a failing run exits with its error, and a working run writes its progress
    /// file and prints one tick for it before it exits.
    /// </summary>
    private sealed class FakeCleaner(IPathResolver paths) : RustProcessHelper(
        NullLogger<RustProcessHelper>.Instance,
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        paths,
        operationTracker: null!)
    {
        public int ExitCode { get; set; }
        public Action? OnStart { get; set; }
        public Action? OnTicked { get; set; }

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            Assert.Equal("cache_cleaner", processLabel);
            var progressFile = Regex.Matches(start.Arguments, "\"([^\"]*)\"")[1].Groups[1].Value;
            OnStart?.Invoke();
            if (ExitCode != 0)
            {
                return new ProcessExecutionResult
                {
                    ExitCode = ExitCode,
                    Error = "Error: failed to enumerate cache root: Input/output error (os error 5)"
                };
            }

            await File.WriteAllTextAsync(
                progressFile,
                JsonSerializer.Serialize(new
                {
                    isProcessing = false,
                    percentComplete = 100.0,
                    status = "completed",
                    stageKey = "signalr.cacheClear.progress",
                    context = new { processed = 1, totalDirs = 1, activeCount = 0 },
                    directoriesProcessed = 1,
                    totalDirectories = 1,
                    bytesDeleted = 4096,
                    filesDeleted = 1,
                    activeDirectories = Array.Empty<string>(),
                    activeCount = 0,
                    undeletedFiles = 0
                }),
                cancellationToken);
            await onProgressEvent!(new RustProgressEvent());
            OnTicked?.Invoke();
            return new ProcessExecutionResult { ExitCode = 0 };
        }
    }
}
