using LancacheManager.Core.Interfaces;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using LancacheManager.Configuration;

namespace LancacheManager.Tests;

/// <summary>
/// Two drift guards over the terminal contract. Both cover failures that already happened and that
/// nothing else can catch: they are agreements between places the compiler cannot compare.
/// </summary>
public sealed partial class OperationTerminalContractTests
{
    [Fact]
    public async Task CacheClearWorkersKeepSeparateCompletionCaptures()
    {
        await using var run = await TerminalRun.CreateAsync();
        var first = await run.StartClearAsync();
        run.Observer.Entries[first.Id].HoldEmit = true;
        await run.ClearProgressAsync(first);
        await run.FinishClearAsync(first, 7, 4096);
        await run.Observer.Entries[first.Id].EmitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(run.Messages.Terminals(first.Id));
        var second = await run.StartClearAsync();
        await run.ClearProgressAsync(second);
        await run.FinishClearAsync(second, 11, 16384);
        run.Observer.Entries[first.Id].EmitRelease.TrySetResult();
        await run.Observer.Entries[first.Id].Emitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var (connection, files, bytes) in new[] { (first, 7, 4096), (second, 11, 16384) })
        {
            var terminal = Assert.Single(run.Messages.Terminals(connection.Id));
            Assert.Equal(connection.Id, terminal.GetProperty("operationId").GetGuid());
            Assert.Equal(files, terminal.GetProperty("filesDeleted").GetInt32());
            Assert.Equal(bytes, terminal.GetProperty("bytesDeleted").GetInt64());
            Assert.Equal(4, terminal.GetProperty("directoriesProcessed").GetInt32());
            Assert.Equal(1, terminal.GetProperty("datasourcesCleared").GetInt32());
            Assert.Equal(1, run.Observer.Entries[connection.Id].Publications);
            Assert.Equal(OperationStatus.Completed, run.State.GetCacheClearOperations().Single(item => item.Id == connection.Id).Status);
        }
        run.AssertHealthy();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheClearExternalTerminalRejectsTheSuccessfulWorker(bool cancelled)
    {
        await using var run = await TerminalRun.CreateAsync();
        var first = await run.StartClearAsync();
        await run.ClearProgressAsync(first);
        run.Tracker.CompleteOperation(first.Id, false, cancelled ? null : "Original clear failure", cancelled: cancelled);
        var frozen = run.Freeze(first.Id);
        var progressCount = run.Messages.ProgressCount(first.Id);
        await run.FinishClearAsync(first, 7, 4096);
        await run.Observer.Attempt(first.Id, "clear.final").Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(frozen, run.Freeze(first.Id));
        Assert.Equal(progressCount, run.Messages.ProgressCount(first.Id));
        Assert.Equal(0, run.Observer.Entries[first.Id].Publications);
        Assert.Equal(1, run.Observer.Entries[first.Id].Cleanups);
        var terminal = Assert.Single(run.Messages.Terminals(first.Id));
        Assert.Equal(cancelled ? "cancelled" : "failed", terminal.GetProperty("status").GetString());
        var saved = run.State.GetCacheClearOperations().Single(item => item.Id == first.Id);
        Assert.Equal(cancelled ? OperationStatus.Cancelled : OperationStatus.Failed, saved.Status);
        Assert.Equal(run.Tracker.GetOperation(first.Id)!.CompletedAt, saved.EndTime);
        run.AssertHealthy();
    }

    [Fact]
    public async Task CacheClearOriginalCleanupPreservesThePubliclyStartedSuccessor()
    {
        await using var run = await TerminalRun.CreateAsync();
        var first = await run.StartClearAsync();
        await run.ClearProgressAsync(first);
        var entry = run.Observer.Entries[first.Id];
        entry.HoldCleanup = true;
        var completing = Task.Run(() => run.Tracker.CompleteOperation(first.Id, false, "Original clear failure"));
        run.Pending.Add(completing);
        await entry.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await run.StartClearAsync();
        await run.ClearProgressAsync(second);
        var frozen = run.Freeze(second.Id);
        entry.CleanupRelease.TrySetResult();
        await completing;
        await run.FinishClearAsync(first, 7, 4096);
        Assert.Equal(frozen, run.Freeze(second.Id));
        Assert.Equal(second.Id, typeof(CacheClearingService).GetField("_currentTrackerOperationId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(run.Clear));
        Assert.Equal(OperationStatus.Running, run.Tracker.GetOperation(second.Id)!.Status);
        await run.FinishClearAsync(second, 11, 16384);
        Assert.Equal(1, entry.Cleanups);
        run.AssertHealthy();
    }

    [Theory]
    [InlineData(CorruptionDetectionMethod.Structural)]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    public async Task CorruptionWorkerPublishesThePersistedSuccessfulSnapshot(CorruptionDetectionMethod method)
    {
        await using var run = await TerminalRun.CreateAsync();
        var connection = await run.StartDetectionAsync(method);
        await run.DetectionProgressAsync(connection, method, "detection.partial", false);
        await run.FinishDetectionAsync(connection, method);
        var metrics = Assert.IsType<CorruptionDetectionMetrics>(run.Tracker.GetOperation(connection.Id)!.Metadata);
        Assert.Equal(100, run.Tracker.GetOperation(connection.Id)!.PercentComplete);
        Assert.NotNull(metrics.ScanId);
        Assert.Equal(1, metrics.CorruptionCounts!["steam"]);
        Assert.Equal(1, metrics.DetectionCounts![method.ToWireString()]);
        Assert.Null(metrics.CurrentProgress);
        Assert.Null(run.OperationState.GetState(connection.Id.ToString()));
        Assert.Equal(1, run.Observer.Entries[connection.Id].Publications);
        Assert.Equal(1, run.Observer.Entries[connection.Id].Cleanups);
        Assert.Equal("completed", Assert.Single(run.Messages.Terminals(connection.Id)).GetProperty("status").GetString());
        if (method == CorruptionDetectionMethod.Structural)
        {
            Assert.Equal(4, metrics.FilesProcessed);
            Assert.True(metrics.StateCommitted);
            Assert.NotNull(metrics.Coverage);
        }
        await run.AssertPersistedAsync(connection, method, metrics.ScanId);
        run.AssertHealthy();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptionExternalTerminalRejectsLateRelayAndSuccessfulFinalizer(bool cancelled)
    {
        await using var run = await TerminalRun.CreateAsync();
        var connection = await run.StartDetectionAsync(CorruptionDetectionMethod.Structural);
        await run.DetectionProgressAsync(connection, CorruptionDetectionMethod.Structural, "detection.partial", false);
        var other = run.Tracker.RegisterOperation(OperationType.GameDetection, "Other detection", new CancellationTokenSource());
        run.OperationState.SaveState(other.ToString(), new LancacheManager.Core.Services.OperationState { Key = other.ToString(), Type = "gameDetection", Status = "running" });
        run.Tracker.CompleteOperation(connection.Id, false, cancelled ? null : "Original detection failure", cancelled: cancelled);
        Assert.NotNull(run.OperationState.GetState(other.ToString()));
        Assert.Null(run.OperationState.GetState(connection.Id.ToString()));
        var frozen = run.Freeze(connection.Id);
        var count = run.Messages.ProgressCount(connection.Id);
        await run.DetectionProgressAsync(connection, CorruptionDetectionMethod.Structural, "detection.late", true);
        await run.FinishDetectionAsync(connection, CorruptionDetectionMethod.Structural);
        Assert.Equal(frozen, run.Freeze(connection.Id));
        Assert.Equal(count, run.Messages.ProgressCount(connection.Id));
        Assert.Equal(0, run.Observer.Entries[connection.Id].Publications);
        Assert.Equal(1, run.Observer.Entries[connection.Id].Cleanups);
        Assert.Equal(cancelled ? "cancelled" : "failed", Assert.Single(run.Messages.Terminals(connection.Id)).GetProperty("status").GetString());
        var metrics = Assert.IsType<CorruptionDetectionMetrics>(run.Tracker.GetOperation(connection.Id)!.Metadata);
        Assert.Null(metrics.CurrentProgress);
        Assert.Null(metrics.ScanId);
        Assert.Null(metrics.CorruptionCounts);
        Assert.Null(metrics.LastDetectionTime);
        Assert.NotNull(run.OperationState.GetState(other.ToString()));
        await run.AssertPersistedAsync(connection, CorruptionDetectionMethod.Structural, null);
        run.Tracker.CompleteOperation(other, true);
        run.OperationState.RemoveState(other.ToString());
        run.AssertHealthy();
    }

    private sealed class TerminalRun : IAsyncDisposable
    {
        private readonly string _root;
        private readonly TestDatabase _database;
        private readonly string _operations;
        private readonly string _pipeName = "terminal-" + Guid.NewGuid().ToString("N");
        private readonly List<TerminalPipe> _connections = [];
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _finished = new();
        private Guid _finishing;
        public List<Task> Pending { get; } = [];
        public UnifiedOperationTracker Tracker { get; }
        public TerminalTracker Observer { get; }
        public TerminalMessages Messages { get; }
        public TerminalContexts Contexts { get; }
        public StateService State { get; }
        public OperationStateService OperationState { get; }
        public CacheClearingService Clear { get; }
        public CorruptionDetectionService Detection { get; }
        public string CachePath { get; }

        public static async Task<TerminalRun> CreateAsync() => new(await TestDatabase.CreateAsync());

        private TerminalRun(TestDatabase database)
        {
            _database = database;
            _root = Path.Combine(Path.GetTempPath(), "terminal-contract-" + Guid.NewGuid().ToString("N"));
            CachePath = Path.Combine(_root, "cache");
            var logs = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logs);
            foreach (var hex in new[] { "aa", "bb", "cc", "dd" }) Directory.CreateDirectory(Path.Combine(CachePath, hex));
            var paths = DispatchProxy.Create<IPathResolver, TerminalPaths>();
            ((TerminalPaths)(object)paths).Inner = new TerminalRoot(_root);
            _operations = paths.GetOperationsDirectory();
            Directory.CreateDirectory(_operations);
            File.WriteAllText(Path.Combine(_operations, "corruption-pipe"), _pipeName);
            File.WriteAllText(Path.Combine(_operations, "cache-clear-pipe"), _pipeName);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "default",
                ["LanCache:DataSources:0:CachePath"] = CachePath,
                ["LanCache:DataSources:0:LogPath"] = logs,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic,
                ["NginxLogRotation:Enabled"] = "false"
            }).Build();
            var processes = new ProcessManager(Logger<ProcessManager>());
            Tracker = new UnifiedOperationTracker(processes, Logger<UnifiedOperationTracker>());
            var forwarded = DispatchProxy.Create<IUnifiedOperationTracker, TerminalTracker>();
            Observer = (TerminalTracker)(object)forwarded;
            Observer.Inner = Tracker;
            var notifications = DispatchProxy.Create<ISignalRNotificationService, TerminalMessages>();
            Messages = (TerminalMessages)(object)notifications;
            Messages.Tracker = Tracker;
            var sources = new DatasourceService(configuration, paths, Logger<DatasourceService>());
            foreach (var source in sources.GetDatasources()) source.CacheWritable = true;
            var capability = new DatasourceCapabilityService(sources);
            State = new StateService(Logger<StateService>(), paths, null!, null!);
            typeof(StateService).GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(State, new AppState { SteamAuth = null });
            OperationState = new OperationStateService(Logger<OperationStateService>(), configuration, State);
            Contexts = new TerminalContexts(database.Options);
            var rust = new RustProcessHelper(Logger<RustProcessHelper>(), processes, paths, forwarded);
            var games = new GameCacheDetectionService(Logger<GameCacheDetectionService>(), paths, OperationState, Contexts,
                new GameCacheDetectionDataService(Contexts, Logger<GameCacheDetectionDataService>()), null!, null!, rust,
                notifications, sources, capability, forwarded, CacheScanGateHarness.Idle());
            Clear = new CacheClearingService(Logger<CacheClearingService>(), notifications, configuration, paths, State,
                rust, sources, forwarded, Contexts, games);
            Detection = new CorruptionDetectionService(Logger<CorruptionDetectionService>(), configuration, paths, rust,
                notifications, sources, Contexts, OperationState, forwarded, capability, CacheScanGateHarness.Idle());
        }

        private TerminalLog<T> Logger<T>() => new(_errors, state =>
        {
            if (state.TryGetValue("{OriginalFormat}", out var template)
                && Equals(template, "[CorruptionDetection] Scan {ScanId} complete: {Services}"))
            {
                Assert.True(state.ContainsKey("ScanId"));
                Assert.True(state.ContainsKey("Services"));
                _finished.GetOrAdd(_finishing, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
        });

        public async Task<TerminalPipe> StartClearAsync()
        {
            var pipe = new TerminalPipe(_pipeName);
            _connections.Add(pipe);
            var id = await Clear.StartCacheClearAsync();
            Assert.NotNull(id);
            await pipe.ConnectAsync(id.Value);
            return pipe;
        }

        public async Task<TerminalPipe> StartDetectionAsync(CorruptionDetectionMethod method)
        {
            var pipe = new TerminalPipe(_pipeName);
            _connections.Add(pipe);
            var id = await Detection.StartDetectionAsync(detectionMethod: method,
                scanMode: method == CorruptionDetectionMethod.Structural ? StructuralScanMode.Full : null);
            await pipe.ConnectAsync(id);
            return pipe;
        }

        public async Task ClearProgressAsync(TerminalPipe pipe)
        {
            await pipe.SendAsync(ClearCheckpoint("clear.partial", false, 2, 128));
            await Messages.Progress(pipe.Id, "clear.partial").Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(25, Tracker.GetOperation(pipe.Id)!.PercentComplete);
        }

        public async Task FinishClearAsync(TerminalPipe pipe, int files, long bytes)
        {
            var disposed = Contexts.Expect(pipe.Id);
            await pipe.SendAsync(ClearCheckpoint("clear.final", true, files, bytes), 0);
            await Observer.Entries[pipe.Id].Attempted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(Observer.Entries[pipe.Id].AttemptSuccess);
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await pipe.Process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(pipe.Process.HasExited);
            Assert.Equal(1, Observer.Entries[pipe.Id].Cleanups);
            if (!Observer.Entries[pipe.Id].HoldEmit)
                await Observer.Entries[pipe.Id].Emitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static object ClearCheckpoint(string stage, bool final, int files, long bytes) => new
        {
            isProcessing = !final,
            percentComplete = final ? 100 : 99,
            status = final ? "completed" : "running",
            stageKey = stage,
            context = new { label = stage },
            directoriesProcessed = final ? 4 : 1,
            totalDirectories = 4,
            bytesDeleted = bytes,
            filesDeleted = files,
            activeDirectories = Array.Empty<string>(),
            activeCount = 0
        };

        public async Task DetectionProgressAsync(TerminalPipe pipe, CorruptionDetectionMethod method, string stage, bool rejected)
        {
            await pipe.SendAsync(DetectionCheckpoint(method, stage, false, rejected));
            if (rejected) await Observer.Attempt(pipe.Id, stage).Task.WaitAsync(TimeSpan.FromSeconds(10));
            else await Messages.Progress(pipe.Id, stage).Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task FinishDetectionAsync(TerminalPipe pipe, CorruptionDetectionMethod method)
        {
            _finishing = pipe.Id;
            var done = _finished.GetOrAdd(pipe.Id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            await pipe.SendAsync(DetectionCheckpoint(method, "detection.final", true, false), 0, Report(pipe, method));
            await Observer.Entries[pipe.Id].Attempted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(Observer.Entries[pipe.Id].AttemptSuccess);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Observer.Attempt(pipe.Id, "detection.final").Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Observer.Entries[pipe.Id].Emitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await pipe.Process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(pipe.Process.HasExited);
        }

        private static object DetectionCheckpoint(CorruptionDetectionMethod method, string stage, bool final, bool late) => new
        {
            status = final ? "completed" : "running",
            stageKey = stage,
            percentComplete = final ? 100 : late ? 75 : 25,
            filesProcessed = final ? 4 : late ? 3 : 1,
            totalFiles = 4,
            context = method == CorruptionDetectionMethod.Structural ? new Dictionary<string, object?>
            {
                ["scanMode"] = "full",
                ["effectiveScanMode"] = "full",
                ["baselineStatus"] = "ready",
                ["stateCommitted"] = final,
                ["resumed"] = false,
                ["filesDiscovered"] = final || late ? 4 : 2,
                ["filesProcessed"] = final ? 4 : late ? 3 : 1,
                ["filesReused"] = 0,
                ["filesInspected"] = final ? 4 : late ? 3 : 1,
                ["filesRevalidated"] = 0,
                ["invalidFiles"] = final ? 1 : 0,
                ["filesPendingRetry"] = 0,
                ["filesPruned"] = 0,
                ["stateEntries"] = final ? 4 : 1
            } : new Dictionary<string, object?> { ["filesProcessed"] = final ? 4 : 1, ["totalFiles"] = 4 }
        };

        private CorruptionReport Report(TerminalPipe pipe, CorruptionDetectionMethod method)
        {
            var start = pipe.Arguments[Array.IndexOf(pipe.Arguments, "--scan-started-utc") + 1];
            var time = DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var candidate = new CorruptionCandidate
            {
                CandidateId = "candidate",
                Service = "steam",
                ExactPaths = [Path.Combine(CachePath, "aa", "00000000000000000000000000000000")]
            };
            if (method == CorruptionDetectionMethod.Structural)
            {
                candidate.Evidence = new StructuralCorruptionEvidence
                {
                    Issues = [StructuralCorruptionIssue.EmptyCacheFile],
                    CacheKeyEncoding = "hex",
                    CacheKey = string.Empty,
                    CacheKeyMd5 = "d41d8cd98f00b204e9800998ecf8427e",
                    CacheVersion = 5,
                    FileLength = 0,
                    Fingerprint = new StructuralFileFingerprint { Device = 1, Inode = 1, Length = 0, ModifiedNanoseconds = 1, ChangedNanoseconds = 1 },
                    DetectedAtUtc = time.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                };
            }
            else
            {
                var observations = Enumerable.Range(0, 3).Select(index => new CandidateObservation
                {
                    RawUrl = "/depot/chunk",
                    Timestamp = time.AddSeconds(index - 2).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
                    ClientIp = $"192.0.2.{index + 1}",
                    Method = "GET",
                    HttpStatus = index % 2 == 0 ? 206 : 200,
                    CacheStatus = "MISS",
                    RawRange = "bytes=1048576-2097151",
                    BytesServed = 1_048_576
                }).ToList();
                candidate.Evidence = new RepeatedMissCorruptionEvidence
                {
                    RawUrl = "/depot/chunk",
                    NormalizedUri = "/depot/chunk",
                    EvidenceCount = 3,
                    ObservedRange = new ObservedByteRange { Kind = "inclusive", Start = 1_048_576, End = 2_097_151 },
                    CacheSlice = new CacheSliceIdentity { Kind = "ranged", Start = 1_048_576, End = 2_097_151 },
                    FirstSeen = observations[0].Timestamp,
                    LastSeen = observations[^1].Timestamp,
                    Observations = observations
                };
            }
            return new CorruptionReport
            {
                ContractVersion = CorruptionReport.SupportedContractVersion,
                DetectionMethod = method,
                ScanStartedUtc = start,
                Settings = method == CorruptionDetectionMethod.Structural
                    ? new CorruptionScanSettings { MinimumStableAgeSeconds = 600, MaximumPrefixBytes = 65_535 }
                    : new CorruptionScanSettings { Threshold = 3, LookbackDays = 30 },
                Candidates = [candidate],
                ServiceCounts = new Dictionary<string, long> { ["steam"] = 1 },
                DetectionCounts = new Dictionary<string, long> { [method.ToWireString()] = 1 },
                Total = 1,
                Coverage = method == CorruptionDetectionMethod.Structural ? new CorruptionScanCoverage
                {
                    FilesSeen = 4,
                    FilesChecked = 2,
                    Consistent = 1,
                    BytesRead = 512,
                    SparseFiles = 0,
                    SkippedByReason = new Dictionary<string, long> { ["recent"] = 2 },
                    IoErrors = 0
                } : null
            };
        }

        public async Task AssertPersistedAsync(TerminalPipe pipe, CorruptionDetectionMethod method, Guid? scanId)
        {
            await using var db = _database.Factory.CreateDbContext();
            var scan = Assert.Single(await db.CachedCorruptionScans.ToListAsync());
            if (scanId.HasValue) Assert.Equal(scanId.Value, scan.ScanId);
            Assert.True(scan.IsCurrent);
            var start = pipe.Arguments[Array.IndexOf(pipe.Arguments, "--scan-started-utc") + 1];
            Assert.Equal(DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal), scan.StartedAtUtc);
            var persisted = Assert.Single(await db.CachedCorruptionDetections.Where(item => item.ServiceName == "steam").ToListAsync());
            Assert.Equal("default", persisted.DatasourceName);
            Assert.Equal(1, persisted.CorruptedChunkCount);
            Assert.Contains("default", persisted.CandidatesJson, StringComparison.Ordinal);
            Assert.Equal(method == CorruptionDetectionMethod.Structural ? CorruptionDetectionMode.Structural : CorruptionDetectionMode.RepeatedMiss, scan.DetectionMode);
        }

        public string Freeze(Guid id)
        {
            var operation = Tracker.GetOperation(id)!;
            return JsonSerializer.Serialize(new
            {
                operation.Status,
                operation.Success,
                operation.Cancelled,
                operation.Message,
                operation.PercentComplete,
                operation.StartedAt,
                operation.CompletedAt,
                Metrics = JsonSerializer.SerializeToElement(operation.Metadata, operation.Metadata!.GetType())
            });
        }

        public void AssertHealthy() => Assert.Empty(_errors);

        public async ValueTask DisposeAsync()
        {
            foreach (var entry in Observer.Entries.Values)
            {
                entry.EmitRelease.TrySetResult();
                entry.CleanupRelease.TrySetResult();
            }
            foreach (var connection in _connections) await connection.DisposeAsync();
            await Task.WhenAll(Pending).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var operation in Tracker.GetActiveOperations().ToList()) Tracker.CompleteOperation(operation.Id, false, cancelled: true);
            await _database.DisposeAsync();
            var root = Path.GetFullPath(_root);
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(root));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class TerminalRoot(string root) : PathResolverBase(NullLogger.Instance)
    {
        protected override string BasePath => root;
        protected override string RustExecutableExtension => OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        public override string ResolvePath(string relativePath) => Path.GetFullPath(Path.Combine(root, relativePath));
        public override string NormalizePath(string path) => Path.GetFullPath(path);
        public override bool IsDockerSocketAvailable() => false;
    }

    public class TerminalPaths : DispatchProxy
    {
        public IPathResolver Inner { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var executable = targetMethod!.Name switch
            {
                nameof(IPathResolver.GetRustCacheCleanerPath) => Path.Combine(AppContext.BaseDirectory, "cache-clear-process", "CacheClearProcess"),
                nameof(IPathResolver.GetRustCorruptionManagerPath) => Path.Combine(AppContext.BaseDirectory, "corruption-process", "CorruptionProcess"),
                _ => null
            };
            if (executable == null) return targetMethod.Invoke(Inner, args);
            if (OperatingSystem.IsWindows()) executable += ".exe";
            Assert.True(File.Exists(executable), $"Built process is missing: {executable}");
            return executable;
        }
    }

    public class TerminalTracker : DispatchProxy
    {
        public UnifiedOperationTracker Inner { get; set; } = null!;
        public ConcurrentDictionary<Guid, TerminalEntry> Entries { get; } = new();
        private readonly ConcurrentDictionary<(Guid, string), TaskCompletionSource> _attempts = new();
        public TaskCompletionSource Attempt(Guid id, string stage) => _attempts.GetOrAdd((id, stage), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            if (method.Name == nameof(IUnifiedOperationTracker.RegisterOperation))
            {
                var id = Guid.Empty;
                var entry = new TerminalEntry();
                var cleanup = (Action?)args![4];
                var emit = (Func<OperationTerminalInfo, Task>?)args[5];
                args[4] = (Action)(() =>
                {
                    Assert.False(Monitor.IsEntered(Inner.GetOperation(id)!));
                    entry.CleanupEntered.TrySetResult();
                    if (entry.HoldCleanup) entry.CleanupRelease.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
                    cleanup?.Invoke();
                    Interlocked.Increment(ref entry.Cleanups);
                    entry.Cleaned.TrySetResult();
                });
                args[5] = (Func<OperationTerminalInfo, Task>)(async terminal =>
                {
                    Assert.False(Monitor.IsEntered(Inner.GetOperation(id)!));
                    entry.EmitEntered.TrySetResult();
                    if (entry.HoldEmit) await entry.EmitRelease.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    if (emit != null) await emit(terminal);
                    entry.Emitted.TrySetResult();
                });
                id = (Guid)method.Invoke(Inner, args)!;
                Entries[id] = entry;
                return id;
            }
            if (method.Name == nameof(IUnifiedOperationTracker.UpdateProgress))
            {
                var id = (Guid)args![0]!;
                Assert.False(Monitor.IsEntered(Inner.GetOperation(id)!));
                Attempt(id, (string)args[2]!).TrySetResult();
            }
            if (method.Name == nameof(IUnifiedOperationTracker.CompleteOperation))
            {
                var id = (Guid)args![0]!;
                var entry = Entries[id];
                var publish = (Action<OperationInfo>?)args[5];
                if (publish != null) args[5] = (Action<OperationInfo>)(operation =>
                {
                    Assert.True(Monitor.IsEntered(operation));
                    publish(operation);
                    Interlocked.Increment(ref entry.Publications);
                });
                var result = method.Invoke(Inner, args);
                entry.AttemptSuccess = (bool)args[1]!;
                entry.Attempted.TrySetResult();
                return result;
            }
            return method.Invoke(Inner, args);
        }
    }

    public sealed class TerminalEntry
    {
        public bool HoldEmit;
        public bool HoldCleanup;
        public bool AttemptSuccess;
        public int Publications;
        public int Cleanups;
        public TaskCompletionSource EmitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource EmitRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Emitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public class TerminalMessages : DispatchProxy
    {
        public UnifiedOperationTracker Tracker { get; set; } = null!;
        private readonly ConcurrentQueue<(Guid Id, bool Terminal, JsonElement Body)> _messages = new();
        private readonly ConcurrentDictionary<(Guid, string), TaskCompletionSource> _progress = new();
        public TaskCompletionSource Progress(Guid id, string stage) => _progress.GetOrAdd((id, stage), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        public JsonElement[] Terminals(Guid id) => _messages.Where(item => item.Id == id && item.Terminal).Select(item => item.Body).ToArray();
        public int ProgressCount(Guid id) => _messages.Count(item => item.Id == id && !item.Terminal);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: >= 2 } && args[1] != null)
            {
                var name = (string)args[0]!;
                var body = JsonSerializer.SerializeToElement(args[1], args[1]!.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (body.TryGetProperty("operationId", out var identity) && identity.ValueKind == JsonValueKind.String)
                {
                    var id = identity.GetGuid();
                    Assert.False(Monitor.IsEntered(Tracker.GetOperation(id)!));
                    if (name.EndsWith("Complete", StringComparison.Ordinal)) _messages.Enqueue((id, true, body));
                    if (name.EndsWith("Progress", StringComparison.Ordinal))
                    {
                        _messages.Enqueue((id, false, body));
                        if (body.TryGetProperty("stageKey", out var stage) && stage.ValueKind == JsonValueKind.String)
                            Progress(id, stage.GetString()!).TrySetResult();
                    }
                }
            }
            return targetMethod!.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    private sealed class TerminalLog<T>(ConcurrentQueue<string> errors, Action<IReadOnlyDictionary<string, object?>> observe) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception != null || logLevel >= LogLevel.Error) errors.Enqueue(formatter(state, exception) + " " + exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> values) observe(values.ToDictionary(item => item.Key, item => item.Value));
        }
    }

    private sealed class TerminalContexts(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        private TaskCompletionSource? _next;
        public TaskCompletionSource Expect(Guid id)
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.Null(Interlocked.Exchange(ref _next, signal));
            return signal;
        }
        public AppDbContext CreateDbContext() => new(options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var signal = Interlocked.Exchange(ref _next, null);
            return Task.FromResult<AppDbContext>(signal == null ? new AppDbContext(options) : new TerminalContext(options, signal));
        }
    }

    private sealed class TerminalContext(DbContextOptions<AppDbContext> options, TaskCompletionSource signal) : AppDbContext(options)
    {
        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); signal.TrySetResult(); }
            catch (Exception exception) { signal.TrySetException(exception); throw; }
        }
    }

    private sealed class TerminalPipe : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        public Guid Id { get; private set; }
        public string[] Arguments { get; private set; } = [];
        public Process? Process { get; private set; }
        public TerminalPipe(string name) => _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 10, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        public async Task ConnectAsync(Guid id)
        {
            Id = id;
            await _pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _reader = new StreamReader(_pipe, leaveOpen: true);
            _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            using var connection = JsonDocument.Parse(line!);
            Arguments = connection.RootElement.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.Contains(id.ToString(), connection.RootElement.GetProperty("progressPath").GetString()!, StringComparison.Ordinal);
            Process = Process.GetProcessById(connection.RootElement.GetProperty("processId").GetInt32());
        }
        public Task SendAsync(object checkpoint, int? exitCode = null, CorruptionReport? report = null) =>
            _writer!.WriteLineAsync(JsonSerializer.Serialize(new { Checkpoint = checkpoint, ExitCode = exitCode, Report = report },
                new JsonSerializerOptions(JsonSerializerDefaults.Web))).WaitAsync(TimeSpan.FromSeconds(10));
        public async ValueTask DisposeAsync()
        {
            if (Process != null)
            {
                if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Process.Dispose();
            }
            if (_writer != null) await _writer.DisposeAsync();
            _reader?.Dispose();
            await _pipe.DisposeAsync();
        }
    }

    [Fact]
    public void TrackerOptionalArgumentsKeepExistingPositionsAndVoidContracts()
    {
        var contract = typeof(IUnifiedOperationTracker);
        Assert.Equal(new[] { "type", "name", "cts", "metadata", "onTerminalCleanup", "onTerminalEmit", "initialStatus", "parentOperationId", "startedAt" },
            contract.GetMethod(nameof(IUnifiedOperationTracker.RegisterOperation))!.GetParameters().Select(parameter => parameter.Name));
        Assert.Equal(new[] { "operationId", "type", "name", "cts", "metadata", "onTerminalCleanup", "onTerminalEmit", "parentOperationId", "startedAt" },
            contract.GetMethod(nameof(IUnifiedOperationTracker.TryRestoreOperation))!.GetParameters().Select(parameter => parameter.Name));
        foreach (var name in new[] { nameof(IUnifiedOperationTracker.CompleteOperation), nameof(IUnifiedOperationTracker.UpdateProgress) })
        {
            var method = contract.GetMethod(name)!;
            Assert.Equal(typeof(void), method.ReturnType);
            var publishing = name == nameof(IUnifiedOperationTracker.CompleteOperation) ? 5 : 3;
            Assert.True(method.GetParameters()[publishing].IsOptional);
            Assert.Equal(typeof(Action<OperationInfo>), method.GetParameters()[publishing].ParameterType);
        }
        var completion = contract.GetMethod(nameof(IUnifiedOperationTracker.CompleteOperation))!.GetParameters();
        Assert.Equal(new[] { "operationId", "success", "error", "cancelled", "skipped", "onCompleting", "commit" }, completion.Select(parameter => parameter.Name));
        Assert.True(completion[^1].IsOptional);
        Assert.Equal(typeof(Action), completion[^1].ParameterType);
        Assert.True(contract.GetMethod(nameof(IUnifiedOperationTracker.GetOperation))!.GetParameters()[1].IsOptional);
    }

    [Fact]
    public void RestoredIdentityAndOptionalStatusFieldsSerializeWithoutChangingLegacyFields()
    {
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var id = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.True(tracker.TryRestoreOperation(id, OperationType.GameDetection, "Detection", new CancellationTokenSource(),
            parentOperationId: parent, startedAt: start));
        using var unadopted = new CancellationTokenSource();
        Assert.False(tracker.TryRestoreOperation(id, OperationType.GameDetection, "Other", unadopted,
            parentOperationId: Guid.NewGuid(), startedAt: start.AddDays(1)));
        unadopted.Cancel();
        var operation = tracker.GetOperation(id)!;
        Assert.Equal(parent, operation.ParentOperationId);
        Assert.Equal(start, operation.StartedAt);
        Assert.Equal("Detection", operation.Name);
        var response = new OperationStatusResponse
        {
            Id = id,
            Active = false,
            PercentComplete = 100,
            Message = null,
            Status = OperationStatus.Failed,
            StartedAt = start,
            ParentOperationId = parent,
            NextOperationId = Guid.NewGuid(),
            NextStatus = OperationStatus.Cancelled,
            Error = "Read failed"
        };
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(response, options));
        Assert.Equal(id, json.RootElement.GetProperty("id").GetGuid());
        Assert.False(json.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(100, json.RootElement.GetProperty("percentComplete").GetDouble());
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(start, json.RootElement.GetProperty("startedAt").GetDateTime());
        Assert.Equal(parent, json.RootElement.GetProperty("parentOperationId").GetGuid());
        Assert.Equal("cancelled", json.RootElement.GetProperty("nextStatus").GetString());
        Assert.Equal("Read failed", json.RootElement.GetProperty("error").GetString());
        var standalone = tracker.RegisterOperation(OperationType.GameDetection, "Standalone", new CancellationTokenSource());
        Assert.Null(tracker.GetOperation(standalone)!.ParentOperationId);
        tracker.CompleteOperation(id, false);
        tracker.CompleteOperation(standalone, true);
    }

    /// <summary>
    /// The backend decides terminal state in <see cref="OperationStatusExtensions.IsTerminal"/>; the
    /// notification bar decides it in its own hand-written TERMINAL_STATUSES list. The TypeScript
    /// status type is an alias of the backend enum, so the compiler checks the VALUES, but nothing
    /// checks that the terminal list is complete. Add a fifth terminal status to one side and the
    /// card silently stops being dismissible. That is how Skipped had to be added, by hand, twice.
    /// </summary>
    [Fact]
    public void TerminalStatusList_MatchesTheBackendTerminalStates()
    {
        var backend = Enum.GetValues<OperationStatus>()
            .Where(status => status.IsTerminal())
            .Select(status => status.ToWireString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var frontend = ReadFrontendTerminalStatuses();

        Assert.Equal(backend, frontend);
    }

    /// <summary>
    /// A terminal record may hide interface members it does not put on the wire, which is deliberate
    /// (see <see cref="IOperationComplete"/>). <c>Cancelled</c> is the one member that may not be
    /// hidden: an explicit implementation is invisible to the serializer, so the browser reads it as
    /// false and routes a cancelled run down the FAILED branch. Both cache scans shipped that way,
    /// hardcoded to <c>false</c> with a comment claiming the scan had no cancellation concept, while
    /// their cards carried a cancel button.
    /// </summary>
    [Fact]
    public void EveryTerminalRecord_SerializesCancelled()
    {
        var offenders = TerminalRecordTypes()
            .Where(type => !SerializesCancelled(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Proves the check above can actually fail. Without this a detector that silently matched
    /// nothing would pass forever and read as a guarantee. <see cref="HiddenCancelledProbe"/> hides
    /// Cancelled exactly the way the two cache scans did.
    /// </summary>
    [Fact]
    public void CancelledDetector_CatchesAnExplicitImplementation()
    {
        Assert.False(SerializesCancelled(typeof(HiddenCancelledProbe)));
        Assert.True(SerializesCancelled(typeof(VisibleCancelledProbe)));
    }

    /// <summary>
    /// A cancelled operation that carries no error must not describe itself as a failure. Callers
    /// used to pass a message purely to dodge the old "Operation failed" default, and the message
    /// they reached for was an attribution the code cannot support.
    /// </summary>
    [Fact]
    public void CancelledOperationWithNoError_DoesNotReadAsAFailure()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);

        var operationId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        tracker.CompleteOperation(operationId, success: false, cancelled: true);

        var operation = tracker.GetOperation(operationId);
        Assert.Equal(OperationStatus.Cancelled, operation!.Status);
        Assert.DoesNotContain("failed", operation.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nothing may claim a cancellation came from a person. Every run links its token to the host's
    /// shutdown token, so a container restart reaches the same catch block as a click and would be
    /// recorded as a user action. This scans sources rather than behaviour because the claim is the
    /// text itself, and it came back repeatedly after being removed from the spots that were noticed.
    /// </summary>
    [Fact]
    public void NoSourceClaimsACancellationCameFromAUser()
    {
        var api = Path.Combine(FindRepositoryRoot(), "Api");
        var offenders = Directory
            .EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path)
                .Contains("cancelled by user", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> TerminalRecordTypes() =>
        typeof(IOperationComplete).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IOperationComplete).IsAssignableFrom(type));

    /// <summary>
    /// True when <c>Cancelled</c> reaches the wire: a public instance property that is not ignored.
    /// An explicit interface implementation is private, so it does not survive this check.
    /// </summary>
    private static bool SerializesCancelled(Type type)
    {
        var property = type.GetProperty(
            nameof(IOperationComplete.Cancelled),
            BindingFlags.Public | BindingFlags.Instance);

        return property != null && property.GetCustomAttribute<JsonIgnoreAttribute>() == null;
    }

    private static string[] ReadFrontendTerminalStatuses()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "Web", "src", "contexts", "notifications", "notificationStatus.ts");
        var declaration = TerminalStatusesRegex().Match(File.ReadAllText(path));
        Assert.True(
            declaration.Success,
            $"TERMINAL_STATUSES declaration not found in {path}. If it moved or was renamed, this guard "
                + "must be pointed at its new home rather than deleted.");

        return QuotedNameRegex()
            .Matches(declaration.Groups[1].Value)
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [GeneratedRegex(@"TERMINAL_STATUSES[^=]*=\s*\[(.*?)\]", RegexOptions.Singleline)]
    private static partial Regex TerminalStatusesRegex();

    [GeneratedRegex(@"'([a-zA-Z]+)'")]
    private static partial Regex QuotedNameRegex();

    /// <summary>Hides Cancelled behind an explicit implementation, so it never serializes.</summary>
    private sealed record HiddenCancelledProbe : IOperationComplete
    {
        public Guid? OperationId => null;
        public bool Success => false;
        public OperationStatus Status => OperationStatus.Failed;
        public string? Error => null;
        bool IOperationComplete.Cancelled => false;
    }

    /// <summary>Carries Cancelled as an ordinary property, the shape every wire record needs.</summary>
    private sealed record VisibleCancelledProbe : IOperationComplete
    {
        public Guid? OperationId => null;
        public bool Success => false;
        public OperationStatus Status => OperationStatus.Cancelled;
        public string? Error => null;
        public bool Cancelled => true;
    }
}
