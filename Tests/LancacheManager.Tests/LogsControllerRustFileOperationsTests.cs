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
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class LogsControllerRustFileOperationsTests
{
    [Fact]
    public async Task ResetToBeginning_IsStateOnlyAndDoesNotLaunchRustAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);

        var result = await fixture.Controller.ResetDatasourceLogPositionAsync(
            "alpha",
            new UpdateLogPositionRequest { Position = 0 });

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Position);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Empty(fixture.RustHelper.CountRequests);
    }

    [Fact]
    public async Task ResetAllToBeginning_IsStateOnlyForEveryDatasourceAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 17);
        fixture.State.SetLogPosition("beta", 19);

        var result = await fixture.Controller.ResetLogPositionAsync(
            new UpdateLogPositionRequest { Position = 0 });

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, response.Position);
        Assert.Equal(0, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(0, fixture.State.GetLogPosition("beta"));
        Assert.Empty(fixture.RustHelper.CountRequests);
    }

    [Fact]
    public async Task ResetToEnd_CountsEachDatasourceOnceAndReturnsAggregateAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.RustHelper.CountHandler = (path, _) => Task.FromResult(
            new LogLineCountResult(path == fixture.AlphaLogPath ? 3 : 5, 1));

        var result = await fixture.Controller.ResetLogPositionAsync(request: null);

        var response = Assert.IsType<LogPositionResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(8, response.Position);
        Assert.Equal(3, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(3, fixture.State.GetLogTotalLines("alpha"));
        Assert.Equal(5, fixture.State.GetLogPosition("beta"));
        Assert.Equal(5, fixture.State.GetLogTotalLines("beta"));
        Assert.Equal(new[] { fixture.AlphaLogPath, fixture.BetaLogPath }, fixture.RustHelper.CountRequests);
    }

    [Fact]
    public async Task ResetToEnd_RustFailureDoesNotOverwriteDatasourceStateAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 12);
        fixture.State.SetLogTotalLines("alpha", 13);
        fixture.RustHelper.CountHandler = (_, _) =>
            throw new RustProcessException("log_service_manager", 1, "fixture failure", "count-lines");

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.ResetDatasourceLogPositionAsync("alpha", request: null));

        Assert.Equal(12, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(13, fixture.State.GetLogTotalLines("alpha"));
    }

    [Fact]
    public async Task ResetToEnd_CancellationDoesNotPersistFallbackZeroAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("alpha", 21);
        fixture.State.SetLogTotalLines("alpha", 22);
        fixture.RustHelper.CountHandler = (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LogLineCountResult(0, 0));
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
    }

    [Fact]
    public async Task ResetToEnd_MultiDatasourceFailureKeepsEarlierSuccessfulCountAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.State.SetLogPosition("beta", 30);
        fixture.State.SetLogTotalLines("beta", 31);
        fixture.RustHelper.CountHandler = (path, _) =>
        {
            if (path == fixture.AlphaLogPath)
            {
                return Task.FromResult(new LogLineCountResult(4, 1));
            }

            throw new RustProcessException("log_service_manager", 1, "fixture failure", "count-lines");
        };

        await Assert.ThrowsAsync<RustProcessException>(() =>
            fixture.Controller.ResetLogPositionAsync(request: null));

        Assert.Equal(4, fixture.State.GetLogPosition("alpha"));
        Assert.Equal(4, fixture.State.GetLogTotalLines("alpha"));
        Assert.Equal(30, fixture.State.GetLogPosition("beta"));
        Assert.Equal(31, fixture.State.GetLogTotalLines("beta"));
    }

    [Fact]
    public async Task GetLogPositions_FirstRunUsesRustLineCountFallbackAsync()
    {
        using var fixture = new ControllerFixture();
        fixture.RustHelper.CountHandler = (path, _) => Task.FromResult(
            new LogLineCountResult(path == fixture.AlphaLogPath ? 2 : 6, 1));

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

    private sealed class ControllerFixture : IDisposable
    {
        private readonly string _root;

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

            Datasources = new DatasourceService(
                configuration,
                pathResolver,
                NullLogger<DatasourceService>.Instance);
            State = CreateStateService(_root, configuration, pathResolver);
            RustHelper = new FakeRustProcessHelper(pathResolver);

            var rustProcessor = new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                pathResolver,
                notifications: null!,
                State,
                serviceProvider: null!,
                RustHelper,
                Datasources,
                operationTracker: null!);
            var nginxRotation = new NginxLogRotationService(
                NullLogger<NginxLogRotationService>.Instance,
                configuration,
                new ProcessManager(NullLogger<ProcessManager>.Instance));

            Controller = new LogsController(
                rustProcessor,
                rustLogRemovalService: null!,
                NullLogger<LogsController>.Instance,
                pathResolver,
                RustHelper,
                Datasources,
                State,
                nginxRotation,
                conflictChecker: null!,
                operationQueue: null!);
        }

        public string AlphaLogPath { get; }
        public string BetaLogPath { get; }
        public DatasourceService Datasources { get; }
        public StateService State { get; }
        public FakeRustProcessHelper RustHelper { get; }
        public LogsController Controller { get; }

        public void Dispose()
        {
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
            cachedState.SetValue(state, new AppState());
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
            (_, _) => Task.FromResult(new LogLineCountResult(0, 0));

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

    private class PathResolverProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (targetMethod.Name == nameof(IPathResolver.ResolvePath))
            {
                var path = Assert.IsType<string>(args![0]);
                return Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            }

            if (targetMethod.Name == nameof(IPathResolver.NormalizePath))
            {
                return Assert.IsType<string>(args![0]);
            }

            if (targetMethod.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }

            if (targetMethod.ReturnType == typeof(bool))
            {
                return true;
            }

            if (targetMethod.ReturnType == typeof(int))
            {
                return 0;
            }

            return null;
        }
    }
}
