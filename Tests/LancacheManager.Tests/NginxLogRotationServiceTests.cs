using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class NginxLogRotationServiceTests
{
    private const string HostSignalCommand =
        "pid=$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || " +
        "pgrep -f 'nginx[:] master' | head -1); if [ -z \"$pid\" ]; then exit 3; fi; " +
        "kill -USR1 \"$pid\"";
    private const string HostProbeCommand =
        "pid=$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || " +
        "pgrep -f 'nginx[:] master' | head -1); if [ -z \"$pid\" ]; then exit 3; fi; " +
        "printf 'nginx-pid-visible\\n'; kill -0 \"$pid\"";

    [Fact]
    public async Task CanReopenNginxAsync_ContainerizedNginxWithBareMetalLogs_ReturnsTrueAsync()
    {
        // The log layout is bare-metal, but nginx itself is containerized and the host signal
        // path is unavailable. Availability must follow the runtime reopen paths, not log layout.
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            dockerSocketAvailable: true);
        service.DetectionResult = ("lancache-monolithic", null);
        service.ProcessResults.Enqueue(new ProcessCommandResult
        {
            ExitCode = 1,
            Error = "kill: Operation not permitted"
        });

        var available = await service.CanReopenNginxAsync();

        Assert.True(available);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Empty(service.Commands);
        Assert.Single(service.ProcessResults);
    }

    [Fact]
    public async Task CanReopenNginxAsync_NoContainerAndHostSignalSucceeds_ReturnsTrueAsync()
    {
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            dockerSocketAvailable: true);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var available = await service.CanReopenNginxAsync();

        Assert.True(available);
        Assert.Equal(1, service.DetectionCalls);
        var invocation = Assert.Single(service.Commands);
        Assert.Equal("host nginx signal probe", invocation.Label);
        Assert.Equal(new[] { "-c", HostProbeCommand }, invocation.ArgumentList);
    }

    [Theory]
    [InlineData("kill: no process found")]
    [InlineData("kill: Operation not permitted")]
    public async Task CanReopenNginxAsync_NoContainerAndHostSignalFails_ReturnsFalseAsync(string error)
    {
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            dockerSocketAvailable: true);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 1, Error = error });

        var available = await service.CanReopenNginxAsync();

        Assert.False(available);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task CanReopenNginxAsync_DockerSocketMissingAndHostSignalSucceeds_ReturnsTrueAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var available = await service.CanReopenNginxAsync();

        Assert.True(available);
        Assert.Equal(0, service.DetectionCalls);
        var invocation = Assert.Single(service.Commands);
        Assert.Equal(new[] { "-c", HostProbeCommand }, invocation.ArgumentList);
    }

    [Fact]
    public async Task CanReopenNginxAsync_DockerSocketMissingAndHostSignalFails_ReturnsFalseAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 1 });

        var available = await service.CanReopenNginxAsync();

        Assert.False(available);
        Assert.Equal(0, service.DetectionCalls);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task CanReopenNginxAsync_BothTargetsCached_DoesNotProbeAgainWithinTtlAsync()
    {
        var timeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            timeProvider,
            dockerSocketAvailable: true);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 1 });

        var first = await service.CanReopenNginxAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(29));
        var second = await service.CanReopenNginxAsync();

        Assert.False(first);
        Assert.False(second);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task CanReopenNginxAsync_HostProbeThrows_ReturnsFalseAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());

        var available = await service.CanReopenNginxAsync();

        Assert.False(available);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_HostPidVisibleButSignalDenied_GrantsSignalPrivilegeAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.ProcessResults.Enqueue(new ProcessCommandResult
        {
            ExitCode = 1,
            Output = "nginx-pid-visible\n",
            Error = "kill: Operation not permitted"
        });

        var result = await service.GetNginxReopenAvailabilityAsync("monolithic");

        Assert.False(result.Available);
        Assert.Equal(NginxReopenHint.GrantSignalPrivilege, result.Hint);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_NoNginxContainerAndHostPidInvisible_EnablesPidHostAsync()
    {
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            dockerSocketAvailable: true);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 3 });

        var result = await service.GetNginxReopenAvailabilityAsync("monolithic");

        Assert.False(result.Available);
        Assert.Equal(NginxReopenHint.EnablePidHost, result.Hint);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_StaleHostPidFile_EnablesPidHostAsync()
    {
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            dockerSocketAvailable: true);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult
        {
            ExitCode = 1,
            Output = "nginx-pid-visible\n",
            Error = "kill: No such process"
        });

        var result = await service.GetNginxReopenAvailabilityAsync("monolithic");

        Assert.False(result.Available);
        Assert.Equal(NginxReopenHint.EnablePidHost, result.Hint);
        Assert.Single(service.Commands);
    }

    [Theory]
    [InlineData("bare_metal", NginxReopenHint.EnablePidHost)]
    [InlineData("mixed", NginxReopenHint.EnablePidHost)]
    [InlineData("monolithic", NginxReopenHint.MountDockerSocket)]
    public async Task GetNginxReopenAvailabilityAsync_SocketMissingAndHostPidInvisible_UsesLayoutFallbackAsync(
        string layout,
        NginxReopenHint expectedHint)
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 3 });

        var result = await service.GetNginxReopenAvailabilityAsync(layout);

        Assert.False(result.Available);
        Assert.Equal(expectedHint, result.Hint);
        Assert.Equal(0, service.DetectionCalls);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_HostProbeThrows_UsesLayoutFallbackAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());

        var result = await service.GetNginxReopenAvailabilityAsync("bare_metal");

        Assert.False(result.Available);
        Assert.Equal(NginxReopenHint.EnablePidHost, result.Hint);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_HintSharesAvailabilityCacheAsync()
    {
        var timeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            timeProvider,
            dockerSocketAvailable: true);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 3 });

        var available = await service.CanReopenNginxAsync();
        timeProvider.Advance(TimeSpan.FromSeconds(29));
        var status = await service.GetNginxReopenAvailabilityAsync("monolithic");

        Assert.False(available);
        Assert.False(status.Available);
        Assert.Equal(NginxReopenHint.EnablePidHost, status.Hint);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Single(service.Commands);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_Available_ReturnsNoHintAsync()
    {
        var service = CreateService(
            new CapturingLogger<NginxLogRotationService>(),
            dockerSocketAvailable: true);
        service.DetectionResult = ("lancache-monolithic", null);

        var result = await service.GetNginxReopenAvailabilityAsync("bare_metal");

        Assert.True(result.Available);
        Assert.Equal(NginxReopenHint.None, result.Hint);
        Assert.Empty(service.Commands);
    }

    [Fact]
    public void DatasourceInfoDto_NginxReopenHint_UsesCamelCaseAndWritesNull()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var unavailableJson = JsonSerializer.Serialize(new DatasourceInfoDto
        {
            NginxReopenAvailable = false,
            NginxReopenHint = NginxReopenHint.GrantSignalPrivilege
        }, options);
        var availableJson = JsonSerializer.Serialize(new DatasourceInfoDto
        {
            NginxReopenAvailable = true,
            NginxReopenHint = null
        }, options);

        Assert.Contains("\"nginxReopenHint\":\"grantSignalPrivilege\"", unavailableJson, StringComparison.Ordinal);
        Assert.Contains("\"nginxReopenHint\":null", availableJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReopenNginxLogsAsync_NoContainer_SignalsHostWithExpectedCommandAsync()
    {
        var logger = new CapturingLogger<NginxLogRotationService>();
        var service = CreateService(logger);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var result = await service.ReopenNginxLogsAsync();

        Assert.True(result.Success);
        Assert.Equal(1, service.DetectionCalls);
        var invocation = Assert.Single(service.Commands);
        Assert.Equal("host nginx log reopen", invocation.Label);
        Assert.Equal("sh", invocation.FileName);
        Assert.Equal(new[] { "-c", HostSignalCommand }, invocation.ArgumentList);
        Assert.True(invocation.RedirectStandardOutput);
        Assert.True(invocation.RedirectStandardError);
        Assert.False(invocation.UseShellExecute);
        Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("host nginx master process", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HostProbeAndSignalCommands_UseSelfMatchProofNginxPatternAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var available = await service.CanReopenNginxAsync();
        var reopenResult = await service.ReopenNginxLogsAsync();

        Assert.True(available);
        Assert.True(reopenResult.Success);
        Assert.Equal(2, service.Commands.Count);
        foreach (var invocation in service.Commands)
        {
            Assert.Equal("sh", invocation.FileName);
            Assert.Equal(2, invocation.ArgumentList.Length);
            Assert.Contains("nginx[:] master", invocation.ArgumentList[1], StringComparison.Ordinal);
            Assert.DoesNotContain("nginx: master", invocation.ArgumentList[1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReopenNginxLogsAsync_HostPidNotVisible_ReturnsPidHostRemedyAsync()
    {
        var logger = new CapturingLogger<NginxLogRotationService>();
        var service = CreateService(logger);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult
        {
            ExitCode = 3,
            Error = string.Empty
        });

        var result = await service.ReopenNginxLogsAsync();

        Assert.False(result.Success);
        Assert.False(result.DockerSocketMissing);
        Assert.Contains("pid: host", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("root", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("CAP_KILL", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("code 3", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ReopenNginxLogsAsync_HostSignalDenied_ReturnsFailureAndThrottlesActionableWarningAsync()
    {
        var logger = new CapturingLogger<NginxLogRotationService>();
        var timeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero));
        var service = CreateService(logger, timeProvider);
        service.DetectionResult = (
            null,
            "Docker socket not mounted. Add /var/run/docker.sock:/var/run/docker.sock to your volumes.");
        EnqueueDeniedResult(service, count: 3);

        var first = await service.ReopenNginxLogsAsync();
        var second = await service.ReopenNginxLogsAsync();

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.True(first.DockerSocketMissing);
        Assert.Contains("Failed to signal host nginx", first.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Operation not permitted", first.ErrorMessage, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("--pid=host", warning.Message, StringComparison.Ordinal);
        Assert.Contains("root", warning.Message, StringComparison.Ordinal);
        Assert.Contains("CAP_KILL", warning.Message, StringComparison.Ordinal);
        Assert.Contains("logrotate", warning.Message, StringComparison.Ordinal);
        Assert.Contains("nginx -s reopen", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var third = await service.ReopenNginxLogsAsync();

        Assert.False(third.Success);
        Assert.Equal(2, logger.Entries.Count(entry => entry.Level == LogLevel.Warning));
        Assert.Equal(3, service.Commands.Count);
    }

    [Fact]
    public async Task ReopenNginxLogsAsync_ContainerFound_KeepsDockerSignalPathAsync()
    {
        var logger = new CapturingLogger<NginxLogRotationService>();
        var service = CreateService(logger);
        service.DetectionResult = ("lancache-monolithic", null);
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var result = await service.ReopenNginxLogsAsync();

        Assert.True(result.Success);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Collection(
            service.Commands,
            invocation =>
            {
                Assert.Equal("docker kill", invocation.Label);
                Assert.Equal("docker", invocation.FileName);
                Assert.Equal("kill --signal=USR1 lancache-monolithic", invocation.Arguments);
                Assert.Empty(invocation.ArgumentList);
            },
            invocation =>
            {
                Assert.Equal("docker exec", invocation.Label);
                Assert.Equal("docker", invocation.FileName);
                Assert.Equal(
                    "exec lancache-monolithic sh -c \"kill -USR1 $(cat /var/run/nginx.pid 2>/dev/null || " +
                    "pgrep -f 'nginx[:] master' | head -1)\"",
                    invocation.Arguments);
                Assert.Empty(invocation.ArgumentList);
            });
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_SoleLancacheSidecarHasNoNginx_ReportsUnavailableAsync()
    {
        // Bare-metal nginx runs on the host; the only "lancache"-named container on the socket
        // is a database sidecar. It must not be mistaken for the reopen target, so availability
        // must fall through to the host signal path and report the pid-host remedy.
        var service = CreateRealDetectionService(dockerSocketAvailable: true);
        service.DockerPsOutput =
            "lancache-manager|ghcr.io/regix1/lancache-manager:dev\nlancache-db|internaldb:16";
        service.NginxCheckExitCode = 1;
        service.HostProbeExitCode = 3;

        var result = await service.GetNginxReopenAvailabilityAsync("bare_metal");

        Assert.False(result.Available);
        Assert.Equal(NginxReopenHint.EnablePidHost, result.Hint);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_SoleLancacheContainerHasNginx_ReportsAvailableAsync()
    {
        // A single lancache-named container that actually runs nginx is still detected, so the
        // added nginx check does not regress legitimate monolithic setups.
        var service = CreateRealDetectionService(dockerSocketAvailable: true);
        service.DockerPsOutput = "lancache|myregistry/lancache:latest";
        service.NginxCheckExitCode = 0;

        var result = await service.GetNginxReopenAvailabilityAsync("bare_metal");

        Assert.True(result.Available);
        Assert.Equal(NginxReopenHint.None, result.Hint);
    }

    private static RealDetectionNginxLogRotationService CreateRealDetectionService(bool dockerSocketAvailable)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:Enabled"] = "true",
                ["NginxLogRotation:ContainerName"] = "auto"
            })
            .Build();

        return new RealDetectionNginxLogRotationService(
            new CapturingLogger<NginxLogRotationService>(),
            configuration,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance)
            {
                DockerSocketAvailable = dockerSocketAvailable
            },
            TimeProvider.System);
    }

    private static TestNginxLogRotationService CreateService(
        CapturingLogger<NginxLogRotationService> logger,
        TimeProvider? timeProvider = null,
        bool dockerSocketAvailable = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:Enabled"] = "true",
                ["NginxLogRotation:ContainerName"] = "auto"
            })
            .Build();

        return new TestNginxLogRotationService(
            logger,
            configuration,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance)
            {
                DockerSocketAvailable = dockerSocketAvailable
            },
            timeProvider ?? TimeProvider.System);
    }

    private static void EnqueueDeniedResult(TestNginxLogRotationService service, int count)
    {
        for (var i = 0; i < count; i++)
        {
            service.ProcessResults.Enqueue(new ProcessCommandResult
            {
                ExitCode = 1,
                Error = "kill: Operation not permitted"
            });
        }
    }

    private sealed class TestNginxLogRotationService : NginxLogRotationService
    {
        public TestNginxLogRotationService(
            ILogger<NginxLogRotationService> logger,
            IConfiguration configuration,
            ProcessManager processManager,
            TestPathResolver pathResolver,
            TimeProvider timeProvider)
            : base(logger, configuration, processManager, pathResolver, timeProvider)
        {
        }

        public (string? ContainerName, string? Error) DetectionResult { get; set; }
        public int DetectionCalls { get; private set; }
        public Queue<ProcessCommandResult> ProcessResults { get; } = new();
        public List<CommandInvocation> Commands { get; } = [];

        protected override Task<(string? ContainerName, string? Error)> FindMonolithicContainerAsync()
        {
            DetectionCalls++;
            return Task.FromResult(DetectionResult);
        }

        protected override Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo startInfo,
            string label)
        {
            Commands.Add(new CommandInvocation(
                label,
                startInfo.FileName,
                startInfo.Arguments,
                startInfo.ArgumentList.ToArray(),
                startInfo.RedirectStandardOutput,
                startInfo.RedirectStandardError,
                startInfo.UseShellExecute));
            return Task.FromResult(ProcessResults.Dequeue());
        }
    }

    private sealed class RealDetectionNginxLogRotationService : NginxLogRotationService
    {
        public RealDetectionNginxLogRotationService(
            ILogger<NginxLogRotationService> logger,
            IConfiguration configuration,
            ProcessManager processManager,
            TestPathResolver pathResolver,
            TimeProvider timeProvider)
            : base(logger, configuration, processManager, pathResolver, timeProvider)
        {
        }

        public string DockerPsOutput { get; set; } = string.Empty;
        public int NginxCheckExitCode { get; set; } = 1;
        public int HostProbeExitCode { get; set; } = 3;

        protected override Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo startInfo,
            string label)
        {
            var result = label switch
            {
                "docker ps" => new ProcessCommandResult { ExitCode = 0, Output = DockerPsOutput },
                "docker exec nginx-check" => new ProcessCommandResult { ExitCode = NginxCheckExitCode },
                "host nginx signal probe" => new ProcessCommandResult { ExitCode = HostProbeExitCode },
                _ => new ProcessCommandResult { ExitCode = 0 }
            };

            return Task.FromResult(result);
        }
    }

    private sealed class TestPathResolver(ILogger logger) : PathResolverBase(logger)
    {
        protected override string BasePath => "/test";
        protected override string RustExecutableExtension => string.Empty;
        public bool DockerSocketAvailable { get; init; }

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => DockerSocketAvailable;
    }

    private sealed record CommandInvocation(
        string Label,
        string FileName,
        string Arguments,
        string[] ArgumentList,
        bool RedirectStandardOutput,
        bool RedirectStandardError,
        bool UseShellExecute);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
