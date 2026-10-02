using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class LogsControllerRustFileOperationsTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ResetToBeginning_IsStateOnlyAndDoesNotLaunchRustAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogTotalLines("alpha", 41);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");

        var result = await fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 0 });

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Position);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
        Assert.Empty(fixture.RustHelper.CountRequests);
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.True(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetAllToBeginning_IsStateOnlyForEveryDatasourceAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogPosition("beta", 19);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");

        var result = await fixture.Controller.ResetLogPositionAsync(
            new UpdateLogPositionRequest { Position = 0 });

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Position);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogPosition("beta"));
        Assert.Empty(fixture.RustHelper.CountRequests);
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.False(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetToEnd_CountsEachDatasourceOnceAndReturnsAggregateAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");
        fixture.RustHelper.CountHandler = (path, _) => Task.FromResult(
            new LogLineCountResult(path == fixture.AlphaLogPath ? 3 : 5, 1, new Dictionary<string, long>()));

        var result = await fixture.Controller.ResetLogPositionAsync(request: null);

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(8, response.Position);
        Assert.Equal(3, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(3, fixture.State.GetLogTotalLines("alpha"));
        Assert.Equal(5, fixture.State.GetLogPosition("beta"));
        Assert.Equal(5, fixture.State.GetLogTotalLines("beta"));
        Assert.Equal(new[] { fixture.AlphaLogPath, fixture.BetaLogPath }, fixture.RustHelper.CountRequests);
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.False(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetToEnd_RustFailureDoesNotOverwriteDatasourceStateAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 12);
        fixture.State.SetLogTotalLines("alpha", 13);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.RustHelper.CountHandler = (_, _) =>
            throw new RustProcessException("log_service_manager", 1, "fixture failure", "count-lines");

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.ResetDatasourceLogPositionAsync("alpha", request: null));

        Assert.Equal(12, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(13, fixture.State.GetLogTotalLines("alpha"));
        Assert.True(File.Exists(fixture.ResumePath("alpha")));
    }

    [Fact]
    public async Task ResetToEnd_CancellationDoesNotPersistFallbackZeroAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 21);
        fixture.State.SetLogTotalLines("alpha", 22);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.RustHelper.CountHandler = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LogLineCountResult(0, 0, new Dictionary<string, long>()));
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Controller.ResetDatasourceLogPositionAsync(
                "alpha",
                request: null,
                cancellation.Token));

        Assert.Equal(21, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(22, fixture.State.GetLogTotalLines("alpha"));
        Assert.True(File.Exists(fixture.ResumePath("alpha")));
    }

    [Fact]
    public async Task ResetToEnd_MultiDatasourceFailureKeepsEarlierSuccessfulCountAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("beta", 30);
        fixture.State.SetLogTotalLines("beta", 31);
        fixture.WriteResume("alpha", "alpha-resume");
        fixture.WriteResume("beta", "beta-resume");
        fixture.RustHelper.CountHandler = (path, _) =>
        {
            if (path == fixture.AlphaLogPath)
            {
                return Task.FromResult(new LogLineCountResult(4, 1, new Dictionary<string, long>()));
            }

            throw new RustProcessException("log_service_manager", 1, "fixture failure", "count-lines");
        };

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.ResetLogPositionAsync(request: null));

        Assert.Equal(4, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(4, fixture.State.GetLogTotalLines("alpha"));
        Assert.Equal(30, fixture.State.GetLogPosition("beta"));
        Assert.Equal(31, fixture.State.GetLogTotalLines("beta"));
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.True(File.Exists(fixture.ResumePath("beta")));
    }

    [Fact]
    public async Task ResetToEnd_HoldsProcessingGateWhileCountRunsAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        fixture.WriteResume("alpha", "alpha-resume");
        var countStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.RustHelper.CountHandler = async (path, _) =>
        {
            countStarted.TrySetResult(true);
            await releaseCount.Task;
            return new LogLineCountResult(
                7,
                1,
                new Dictionary<string, long> { ["access.log"] = 7 });
        };

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 });
        await countStarted.Task;

        Assert.True(fixture.Processor.IsProcessing);
        Assert.False(await fixture.Processor.StartProcessingAsync(
            fixture.AlphaLogPath,
            liveIngest: true,
            datasourceName: "alpha"));
        var conflict = await fixture.Checker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            CancellationToken.None);
        Assert.NotNull(conflict);
        Assert.Equal(nameof(OperationType.LogProcessing), conflict!.ActiveOperationType);

        releaseCount.TrySetResult(true);
        var result = await reset;

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.False(File.Exists(fixture.ResumePath("alpha")));
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Empty(fixture.Tracker.GetActiveOperations());
    }

    [Fact]
    public async Task ProcessingCleanupCannotClearResetReservationAsync()
    {
        using var fixture = new ControllerFixture();
        var reservation = await fixture.Processor.TryReserveProcessingGateAsync();
        Assert.NotNull(reservation);

        SetRunFlag(fixture.Processor, false);

        Assert.True(fixture.Processor.IsProcessing);
        fixture.Processor.ReleaseProcessingGate(reservation.Value);
        Assert.False(fixture.Processor.IsProcessing);
    }

    [Fact]
    public void StaleReservationReleaseCannotClearRunningPass()
    {
        using var fixture = new ControllerFixture();
        SetRunFlag(fixture.Processor, true);

        fixture.Processor.ReleaseProcessingGate(Guid.NewGuid());

        Assert.True(fixture.Processor.IsProcessing);
        SetRunFlag(fixture.Processor, false);
    }

    [Fact]
    public async Task ResetToEnd_ActiveLogProcessingConflictDoesNotCountAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.Tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Existing log processing",
            new CancellationTokenSource());

        var result = await fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 });

        var refusal = Assert.IsType<ErrorResponse>(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Equal("errors.logs.processingActive", refusal.StageKey);
        Assert.Empty(fixture.RustHelper.CountRequests);
    }

    [Theory]
    [InlineData(OperationType.DownloadHistoryUpgrade, false)]
    [InlineData(OperationType.DownloadHistoryUpgrade, true)]
    [InlineData(OperationType.CacheSizeScan, false)]
    [InlineData(OperationType.CacheSizeScan, true)]
    public async Task ResetToEnd_RunsBesideAJobThatIsNotLogProcessingAsync(OperationType activeType, bool resetAll)
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogPosition("beta", 19);
        fixture.RustHelper.CountHandler = (_, _) => Task.FromResult(
            new LogLineCountResult(3, 1, new Dictionary<string, long>()));
        fixture.Tracker.RegisterOperation(activeType, "Other job", new CancellationTokenSource());

        var result = resetAll
            ? await fixture.Controller.ResetLogPositionAsync(request: null)
            : await fixture.Controller.ResetDatasourceLogPositionAsync("alpha", request: null);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(resetAll ? 3 : 19, fixture.State.GetLogPosition("beta"));
    }

    [Fact]
    public async Task ResetToEnd_WaitsForAStepThenHoldsTheLogsWhileItCountsAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        fixture.RustHelper.CountHandler = async (_, _) =>
        {
            // Counting outside the lock would let a step lower positions that this write then undoes.
            await fixture.RepairOwner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
            return new LogLineCountResult(9, 1, new Dictionary<string, long> { ["access.log"] = 9 });
        };
        var step = await HoldStepAsync(fixture);

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 });
        Assert.False(reset.IsCompleted);
        Assert.Empty(fixture.RustHelper.CountRequests);

        await step.DisposeAsync();
        Assert.IsType<OkObjectResult>(await reset.WaitAsync(_wait));
        Assert.Equal(9, fixture.State.GetLogPosition("alpha"));
        Assert.False(fixture.Processor.IsProcessing);
        await fixture.RepairOwner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    public async Task Reset_WaitsForAStepBeforeRefusingForThePassBehindItAsync(long position)
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        var step = await HoldStepAsync(fixture);
        // A live pass that started during the step has set IsProcessing and waits at the lock.
        SetRunFlag(fixture.Processor, true);
        var pass = fixture.RepairOwner.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Ingest,
            CancellationToken.None);

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = position });
        Assert.False(reset.IsCompleted);

        await step.DisposeAsync();
        await using var passLock = await pass.WaitAsync(_wait);
        var result = await reset.WaitAsync(_wait);

        var refusal = Assert.IsType<ErrorResponse>(Assert.IsType<ConflictObjectResult>(result).Value);
        Assert.Equal("errors.logs.processingActive", refusal.StageKey);
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.Empty(fixture.RustHelper.CountRequests);
        SetRunFlag(fixture.Processor, false);
    }

    [Fact]
    public async Task ResetToEnd_CancelledWhileWaitingForTheLogsLeavesProcessingFreeAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 7);
        using var cancellation = new CancellationTokenSource();
        // A step that holds the logs still waits for the running speed tracker child to exit.
        Assert.True(fixture.RepairOwner.TryBeginSpeedTrackerRun());

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 1 },
            cancellation.Token);
        await fixture.RepairOwner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
        Assert.True(fixture.Processor.IsProcessing);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reset.WaitAsync(_wait));

        Assert.False(fixture.Processor.IsProcessing);
        Assert.Empty(fixture.Tracker.GetActiveOperations());
        Assert.Empty(fixture.RustHelper.CountRequests);
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        await fixture.RepairOwner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
        fixture.RepairOwner.EndSpeedTrackerRun();
    }

    [Fact]
    public async Task ResetToBeginning_WaitsForAStepHoldingTheLogsAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        var step = await HoldStepAsync(fixture);

        var reset = fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 0 });
        Assert.False(reset.IsCompleted);
        Assert.Equal(17, fixture.State.GetLogPosition("alpha"));

        await step.DisposeAsync();
        Assert.IsType<OkObjectResult>(await reset.WaitAsync(_wait));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
    }

    [Fact]
    public async Task GetLogPositions_FirstRunUsesRustLineCountFallbackAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.RustHelper.CountHandler = (path, _) => Task.FromResult(
            new LogLineCountResult(path == fixture.AlphaLogPath ? 2 : 6, 1, new Dictionary<string, long>()));

        var result = await fixture.Controller.GetLogPositionsAsync();

        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var positions = json.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, positions[0].GetProperty("totalLines").GetInt64());
        Assert.Equal(6, positions[1].GetProperty("totalLines").GetInt64());
        Assert.Equal(new[] { fixture.AlphaLogPath, fixture.BetaLogPath }, fixture.RustHelper.CountRequests);
    }

    [Fact]
    public async Task DeleteLogFile_UsesRustThenResetsStateAndToleratesNginxReopenFailureAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.State.SetLogTotalLines("alpha", 9);
        fixture.RustHelper.DeleteHandler = (path, _) =>
        {
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            return Task.FromResult(new LogFileDeletionResult(bytes));
        };

        var result = await fixture.Controller.DeleteLogFileAsync("alpha");

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new[] { logPath }, fixture.RustHelper.DeleteRequests);
        Assert.False(File.Exists(logPath));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_WaitsForAStepThenHoldsTheLogsWhileDeletingAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "sixsix");
        fixture.State.SetLogPosition("alpha", 9);
        fixture.RustHelper.DeleteHandler = async (path, _) =>
        {
            await fixture.RepairOwner.WaitForLogStepAsync(active: true, CancellationToken.None).WaitAsync(_wait);
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            return new LogFileDeletionResult(bytes);
        };
        var step = await HoldStepAsync(fixture);

        var delete = fixture.Controller.DeleteLogFileAsync("alpha");
        Assert.False(delete.IsCompleted);
        Assert.Empty(fixture.RustHelper.DeleteRequests);
        Assert.Equal(9, fixture.State.GetLogPosition("alpha"));

        await step.DisposeAsync();
        Assert.IsType<OkObjectResult>(await delete.WaitAsync(_wait));
        Assert.False(File.Exists(logPath));
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        await fixture.RepairOwner.WaitForLogStepAsync(active: false, CancellationToken.None).WaitAsync(_wait);
    }

    [Fact]
    public async Task DeleteLogFile_RustFailureLeavesFileAndStateUntouchedAsync()
    {
        using var fixture = new ControllerFixture();
        var logPath = Path.Combine(fixture.AlphaLogPath, "access.log");
        await File.WriteAllTextAsync(logPath, "keep");
        fixture.State.SetLogPosition("alpha", 7);
        fixture.State.SetLogTotalLines("alpha", 8);
        fixture.RustHelper.DeleteHandler = (_, _) =>
            throw new RustProcessException("log_service_manager", 1, "fixture failure", "delete-file");

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.DeleteLogFileAsync("alpha"));

        Assert.True(File.Exists(logPath));
        Assert.Equal(7, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(8, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task DeleteLogFile_MissingFileReturnsNotFoundWithoutLaunchingRustAsync()
    {
        using var fixture = new ControllerFixture();

        var result = await fixture.Controller.DeleteLogFileAsync("alpha");

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Empty(fixture.RustHelper.DeleteRequests);
    }

    private static Task<LogFileLock> HoldStepAsync(ControllerFixture fixture) =>
        fixture.RepairOwner.LockLogFilesAsync(
            null,
            OperationType.GameRemoval,
            LogFileLockKind.Rewrite,
            CancellationToken.None);

    private static void SetRunFlag(RustLogProcessorService processor, bool value)
    {
        typeof(RustLogProcessorService)
            .GetProperty(nameof(RustLogProcessorService.IsProcessing))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(processor, new object[] { value });
    }

    private sealed class ControllerFixture : IDisposable
    {
        private readonly string _root;
        private readonly ServiceProvider _services;
        private readonly OperationStateService _repairOwner;

        public ControllerFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"logs-controller-rust-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            AlphaLogPath = Path.Combine(_root, "alpha-logs");
            BetaLogPath = Path.Combine(_root, "beta-logs");
            Directory.CreateDirectory(AlphaLogPath);
            Directory.CreateDirectory(BetaLogPath);
            Directory.CreateDirectory(Path.Combine(_root, "alpha-cache"));
            Directory.CreateDirectory(Path.Combine(_root, "beta-cache"));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(_root, "alpha-cache"),
                    ["LanCache:DataSources:0:LogPath"] = AlphaLogPath,
                    ["LanCache:DataSources:0:Enabled"] = "true",
                    ["LanCache:DataSources:1:Name"] = "beta",
                    ["LanCache:DataSources:1:CachePath"] = Path.Combine(_root, "beta-cache"),
                    ["LanCache:DataSources:1:LogPath"] = BetaLogPath,
                    ["LanCache:DataSources:1:Enabled"] = "true",
                    ["NginxLogRotation:Enabled"] = "false"
                })
                .Build();

            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = _root;
            OperationsPath = pathResolver.GetOperationsDirectory();
            Directory.CreateDirectory(OperationsPath);

            Datasources = new DatasourceService(
                configuration,
                pathResolver,
                NullLogger<DatasourceService>.Instance);
            State = CreateStateService(_root, configuration, pathResolver);
            RustHelper = new FakeRustProcessHelper(pathResolver);

            Tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            OperationStateService? repairOwner = null;
            RustLogProcessorService? processor = null;
            _services = new ServiceCollection()
                .AddSingleton(_ => repairOwner!)
                .AddSingleton(_ => processor!)
                .BuildServiceProvider();
            _repairOwner = repairOwner = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                configuration,
                State,
                _services.GetRequiredService<IServiceScopeFactory>(),
                DispatchProxy.Create<IHostApplicationLifetime, NullReturningProxy>(),
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                Tracker);
            _repairOwner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Checker = new OperationConflictChecker(
                Tracker,
                _repairOwner,
                NullLogger<OperationConflictChecker>.Instance);

            Processor = processor = new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                pathResolver,
                notifications: null!,
                State,
                _services,
                RustHelper,
                Datasources,
                Tracker);
            var nginxRotation = new NginxLogRotationService(
                NullLogger<NginxLogRotationService>.Instance,
                configuration,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver);

            Controller = new LogsController(
                Processor,
                rustLogRemovalService: null!,
                NullLogger<LogsController>.Instance,
                pathResolver,
                RustHelper,
                Datasources,
                State,
                nginxRotation,
                Checker,
                operationQueue: null!,
                _repairOwner);
        }

        public string AlphaLogPath { get; }
        public string BetaLogPath { get; }
        public string OperationsPath { get; }
        public DatasourceService Datasources { get; }
        public StateService State { get; }
        public FakeRustProcessHelper RustHelper { get; }
        public RustLogProcessorService Processor { get; }
        public UnifiedOperationTracker Tracker { get; }
        public OperationConflictChecker Checker { get; }
        public LogsController Controller { get; }
        public OperationStateService RepairOwner => _repairOwner;

        public string ResumePath(string datasourceName) =>
            Path.Combine(OperationsPath, $"rust_resume_{datasourceName}.json");

        public void WriteResume(string datasourceName, string contents) =>
            File.WriteAllText(ResumePath(datasourceName), contents);

        public void Dispose()
        {
            foreach (var operation in Tracker.GetActiveOperations())
            {
                Tracker.CompleteOperation(operation.Id, success: false, error: "Disposed test fixture");
            }
            _repairOwner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private static StateService CreateStateService(
            string root,
            IConfiguration configuration,
            IPathResolver pathResolver)
        {
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "dp-keys")));
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                configuration,
                pathResolver);
            var encryption = new SecureStateEncryptionService(
                dataProtection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance,
                pathResolver,
                encryption);
            var state = new StateService(
                NullLogger<StateService>.Instance,
                pathResolver,
                encryption,
                steamAuthStorage);

            var cachedState = typeof(StateService).GetField(
                "_cachedState",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            cachedState.SetValue(state, new AppState { SetupCompleted = true });
            return state;
        }
    }

    private sealed class FakeRustProcessHelper : RustProcessHelper
    {
        public FakeRustProcessHelper(IPathResolver pathResolver)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                pathResolver,
                operationTracker: null!)
        {
        }

        public List<string> CountRequests { get; } = new();
        public List<string> DeleteRequests { get; } = new();

        public Func<string, CancellationToken, Task<LogLineCountResult>> CountHandler { get; set; } =
            (_, _) => Task.FromResult(new LogLineCountResult(0, 0, new Dictionary<string, long>()));

        public Func<string, CancellationToken, Task<LogFileDeletionResult>> DeleteHandler { get; set; } =
            (_, _) => Task.FromResult(new LogFileDeletionResult(0));

        public override Task<LogLineCountResult> CountLogLinesAsync(
            string logsPath,
            CancellationToken cancellationToken = default)
        {
            CountRequests.Add(logsPath);
            return CountHandler(logsPath, cancellationToken);
        }

        public override Task<LogFileDeletionResult> DeleteLogFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            DeleteRequests.Add(filePath);
            return DeleteHandler(filePath, cancellationToken);
        }
    }

}
