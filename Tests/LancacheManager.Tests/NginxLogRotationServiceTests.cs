using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class NginxLogRotationServiceTests
{
    private const string HostProbeCommand =
        "pid=$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || " +
        "pgrep -f 'nginx[:] master' | head -1); if [ -z \"$pid\" ]; then exit 3; fi; " +
        "printf 'nginx-pid-visible\\n'; kill -0 \"$pid\"";

    [Fact]
    public async Task PrepareReopenCheckAsync_UnsupportedMappedDockerWriter_DeniesBeforePublicationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-impl-a-docker-denial-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NginxLogRotation:ContainerName"] = "alpha,beta"
        }).Build();
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            configuration,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ReplaceDockerLogs = false,
            ProbeHostWriters = false
        };
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "alpha\nbeta\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = $"{logs}|/logs\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "C:\\unrelated|/logs\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "1|10\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "2|20\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        var source = new ResolvedDatasource
        {
            Name = "alpha",
            CachePath = root,
            ConfiguredLogPath = logs,
            LogPath = logs,
            LogFilePath = target,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            service.PrepareReopenCheckAsync(new[] { source }, new[] { target }, expectsPublication: true));

        Assert.Equal("management.nginxReopen.windowsDockerUnsupported", error.StageKey);
        Assert.Equal("line", await File.ReadAllTextAsync(target));
        var operations = Path.Combine(root, "operations");
        Assert.True(!Directory.Exists(operations) ||
            !Directory.EnumerateFiles(operations, "nginx_log_*").Any());
        Assert.DoesNotContain(service.Commands, command => command.Label.Contains("signal", StringComparison.Ordinal));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task GetNginxReopenAvailabilityAsync_UnsupportedMappedDockerWriter_ReturnsLinuxHintAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ds-impl-a-docker-advisory-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NginxLogRotation:ContainerName"] = "alpha"
        }).Build();
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            configuration,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ReplaceDockerLogs = false,
            ProbeHostWriters = false
        };
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "alpha\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = $"{logs}|/logs\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "1|10\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        var source = new ResolvedDatasource
        {
            Name = "alpha",
            CachePath = root,
            ConfiguredLogPath = logs,
            LogPath = logs,
            LogFilePath = target,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };

        var result = await service.GetNginxReopenAvailabilityAsync(source);

        Assert.False(result.Available);
        Assert.Equal(NginxReopenRequirement.Required, result.Requirement);
        Assert.False(result.CheckOnAction);
        Assert.Equal(NginxReopenHint.UseLinuxManager, result.Hint);
        Assert.Equal("line", await File.ReadAllTextAsync(target));
        Directory.Delete(root, recursive: true);
    }

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
            NginxReopenRequirement = NginxReopenRequirement.Unknown,
            NginxReopenCheckOnAction = true,
            NginxReopenHint = NginxReopenHint.GrantSignalPrivilege
        }, options);
        var availableJson = JsonSerializer.Serialize(new DatasourceInfoDto
        {
            NginxReopenAvailable = true,
            NginxReopenHint = null
        }, options);

        Assert.Contains("\"nginxReopenHint\":\"grantSignalPrivilege\"", unavailableJson, StringComparison.Ordinal);
        Assert.Contains("\"nginxReopenRequirement\":\"unknown\"", unavailableJson, StringComparison.Ordinal);
        Assert.Contains("\"nginxReopenCheckOnAction\":true", unavailableJson, StringComparison.Ordinal);
        Assert.Contains("\"nginxReopenHint\":null", availableJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_AutoDiscoverySkipsForeignObserverAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-owned-writer-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = false,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "observer\nlancache-manager-writer\n"
            },
            "docker nginx mount inspection" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"{logs}|/logs\n"
            },
            "docker nginx writer identity" when command.Arguments.Contains("observer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = "71|foreign\n" },
            "docker nginx writer identity" =>
                new ProcessCommandResult { ExitCode = 0, Output = "72|owned\n" },
            "docker nginx writer ownership" when command.Arguments.Contains("observer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 4 },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "docker nginx verified reopen" when command.Arguments.Contains("observer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 23, Error = "foreign writer" },
            "docker nginx verified reopen" => new ProcessCommandResult { ExitCode = 0 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        await using (var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: false))
        {
            var result = await service.CompleteReopenCheckAsync(check, physicalChange: true);

            var writer = Assert.Single(check.Writers);
            Assert.Equal("lancache-manager-writer", writer.Name);
            Assert.Equal(72, writer.ProcessId);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.DoesNotContain(service.Commands, command =>
                command.Label == "docker nginx verified reopen" &&
                command.Arguments.Contains("observer", StringComparison.Ordinal));
            Assert.Contains(service.Commands, command =>
                command.Label == "docker nginx verified reopen" &&
                command.Arguments.Contains("lancache-manager-writer", StringComparison.Ordinal));
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_DifferentMountDestinationsSelectsOwnedWriterAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-mapped-writer-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var identity = NginxWriterProbe.ReadIdentity(target);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "manager-observer\nruntime-manager\nwriter\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[101]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/fixture/logs|{logs}\n" },
            "docker nginx mount inspection" =>
                new ProcessCommandResult { ExitCode = 0, Output = "/fixture/logs|/data/logs\n" },
            "docker manager mount namespace" when command.ArgumentList.Contains("runtime-manager") =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[101]\n" },
            "docker nginx file identity" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"{identity.First}|{identity.Second}\n"
            },
            "docker nginx writer identity" when command.Arguments.Contains("writer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = "72|owned\n" },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 0, Output = "71|foreign\n" },
            "docker nginx writer ownership" when command.Arguments.Contains("writer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0 },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 4 },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            "docker nginx verified reopen" when command.Arguments.Contains("writer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0 },
            "docker nginx verified reopen" => new ProcessCommandResult { ExitCode = 23, Error = "foreign writer" },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        await using (var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: false))
        {
            var writer = Assert.Single(check.Writers);
            Assert.Equal("writer", writer.Name);
            Assert.Equal(NginxReopenRequirement.Required, check.Requirement);
            var stat = Assert.Single(service.Commands, command =>
                command.Label == "docker nginx file identity" &&
                command.ArgumentList.Contains("writer"));
            Assert.Equal("/data/logs/access.log", stat.ArgumentList[^1]);

            var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.DoesNotContain(service.Commands, command =>
                command.Label == "docker nginx verified reopen" &&
                !command.Arguments.Contains("writer", StringComparison.Ordinal));
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_ExplicitWriterUsesManagerInventoryMountsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-explicit-mapped-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var identity = NginxWriterProbe.ReadIdentity(target);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "writer"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "runtime-manager\nwriter\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[102]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/fixture/logs|{logs}\n" },
            "docker nginx mount inspection" =>
                new ProcessCommandResult { ExitCode = 0, Output = "/fixture/logs|/data/logs\n" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[102]\n" },
            "docker nginx file identity" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"{identity.First}|{identity.Second}\n"
            },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 0, Output = "72|owned\n" },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        await using (var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: false))
        {
            Assert.Equal("writer", Assert.Single(check.Writers).Name);
        }
        Assert.Contains(service.Commands, command => command.Label == "docker nginx writer list");
        Assert.DoesNotContain(service.Commands, command =>
            command.Label == "docker nginx writer identity" &&
            command.Arguments.Contains("runtime-manager", StringComparison.Ordinal));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_NestedAndIndependentMountsPreserveSuffixesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-nested-mounts-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "alpha");
        var serviceLogs = Path.Combine(alpha, "service");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(serviceLogs);
        Directory.CreateDirectory(beta);
        var alphaTarget = Path.Combine(serviceLogs, "access.log");
        var betaTarget = Path.Combine(beta, "access.log");
        await File.WriteAllTextAsync(alphaTarget, "alpha");
        await File.WriteAllTextAsync(betaTarget, "beta");
        var alphaIdentity = NginxWriterProbe.ReadIdentity(alphaTarget);
        var betaIdentity = NginxWriterProbe.ReadIdentity(betaTarget);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "writer"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "runtime-manager\nwriter\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[103]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"/host/alpha|{alpha}\n/host/alpha-service|{serviceLogs}\n" +
                        $"/var/lib/docker/volumes/beta/_data|{beta}\n"
                },
            "docker nginx mount inspection" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "/host/alpha|/data/alpha\n/host/alpha-service|/data/service\n" +
                    "/var/lib/docker/volumes/beta/_data|/data/beta\n"
            },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[103]\n" },
            "docker nginx file identity" when command.ArgumentList[^1] == "/data/service/access.log" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{alphaIdentity.First}|{alphaIdentity.Second}\n"
                },
            "docker nginx file identity" when command.ArgumentList[^1] == "/data/beta/access.log" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{betaIdentity.First}|{betaIdentity.Second}\n"
                },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 0, Output = "73|owned\n" },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            "docker nginx verified reopen" => new ProcessCommandResult { ExitCode = 0 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var sources = new[]
        {
            CreateDatasource("alpha", root, serviceLogs, alphaTarget),
            CreateDatasource("beta", root, beta, betaTarget)
        };

        await using (var check = await service.PrepareReopenCheckAsync(
            sources,
            new[] { alphaTarget, betaTarget },
            expectsPublication: false))
        {
            Assert.Equal("writer", Assert.Single(check.Writers).Name);
            Assert.Empty(check.Proofs);
            Assert.Equal(2, check.OriginalIdentities.Count);

            var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

            Assert.True(result.Success, result.ErrorMessage);
        }
        var statPaths = service.Commands
            .Where(command => command.Label == "docker nginx file identity")
            .Select(command => command.ArgumentList[^1])
            .ToList();
        Assert.Equal(new[] { "/data/service/access.log", "/data/beta/access.log" }, statPaths);
        Assert.Single(service.Commands, command => command.Label == "docker nginx writer identity");
        Assert.Single(service.Commands, command => command.Label == "docker nginx writer ownership");
        Assert.Single(service.Commands, command => command.Label == "docker nginx verified reopen");
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData("stat-exit")]
    [InlineData("stat-malformed")]
    [InlineData("namespace")]
    [InlineData("partial")]
    [InlineData("different")]
    public async Task PrepareReopenCheckAsync_PerFileCoverageDoesNotUseAnotherPathsWriterAsync(string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-file-coverage-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        var alphaTarget = Path.Combine(alpha, "access.log");
        var betaTarget = Path.Combine(beta, "access.log");
        await File.WriteAllTextAsync(alphaTarget, "alpha");
        await File.WriteAllTextAsync(betaTarget, "beta");
        var alphaIdentity = NginxWriterProbe.ReadIdentity(alphaTarget);
        var betaIdentity = NginxWriterProbe.ReadIdentity(betaTarget);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "writer"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = mode == "namespace" ? "manager-a\nmanager-b\nwriter\n" : "manager-a\nwriter\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[201]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("manager-a", StringComparison.Ordinal) =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = mode is "partial" or "namespace"
                        ? $"/host/alpha|{alpha}\n"
                        : $"/host/alpha|{alpha}\n/host/beta|{beta}\n"
                },
            "docker nginx mount inspection" when command.Arguments.Contains("manager-b", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/host/beta|{beta}\n" },
            "docker nginx mount inspection" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "/host/alpha|/data/alpha\n/host/beta|/data/beta\n"
            },
            "docker manager mount namespace" when command.ArgumentList.Contains("manager-b") =>
                new ProcessCommandResult { ExitCode = 17, Error = "beta namespace denied" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[201]\n" },
            "docker nginx writer identity" =>
                new ProcessCommandResult { ExitCode = 0, Output = "72|owned\n" },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "docker nginx file identity" when command.ArgumentList[^1] == "/data/alpha/access.log" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{alphaIdentity.First}|{alphaIdentity.Second}\n"
                },
            "docker nginx file identity" when mode == "stat-exit" =>
                new ProcessCommandResult { ExitCode = 17, Error = "beta identity denied" },
            "docker nginx file identity" when mode == "stat-malformed" =>
                new ProcessCommandResult { ExitCode = 0, Output = "not-an-identity\n" },
            "docker nginx file identity" when mode == "different" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{betaIdentity.First}|{betaIdentity.Second + 1}\n"
                },
            "docker nginx file identity" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{betaIdentity.First}|{betaIdentity.Second}\n"
                },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var sources = new[]
        {
            CreateDatasource("alpha", root, alpha, alphaTarget),
            CreateDatasource("beta", root, beta, betaTarget)
        };
        NginxReopenCheck? check = null;
        Exception? failure = null;
        try
        {
            try
            {
                check = await service.PrepareReopenCheckAsync(
                    sources,
                    new[] { alphaTarget, betaTarget },
                    expectsPublication: true);
            }
            catch (Exception error)
            {
                failure = error;
            }

            if (mode is "stat-exit" or "stat-malformed" or "namespace")
            {
                Assert.Null(check);
                Assert.NotNull(failure);
                if (mode == "stat-exit")
                {
                    Assert.Contains("beta identity denied", failure.Message, StringComparison.Ordinal);
                }
                else if (mode == "stat-malformed")
                {
                    Assert.Contains("invalid output", failure.Message, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Contains("beta namespace denied", failure.Message, StringComparison.Ordinal);
                }
                Assert.Empty(Directory.GetFiles(root, "nginx_log_check_*.json", SearchOption.AllDirectories));
                Assert.DoesNotContain(service.Commands, command => command.Label == "docker nginx verified reopen");
            }
            else
            {
                Assert.Null(failure);
                Assert.NotNull(check);
                Assert.Equal(NginxReopenRequirement.Required, check.Requirement);
                Assert.Equal("writer", Assert.Single(check.Writers).Name);
                var proof = Assert.Single(check.Proofs);
                Assert.Equal(betaTarget, proof.Path);
                Assert.True(proof.Validate());
                Assert.Equal(2, check.OriginalIdentities.Count);
                Assert.Equal(alphaIdentity, check.OriginalIdentities[alphaTarget]);
                Assert.Equal(proof.Identity, check.OriginalIdentities[betaTarget]);
                service.ValidateReopenCheck(check);
                var publication = JsonSerializer.Deserialize<NginxPublicationCheckFile>(
                    await File.ReadAllTextAsync(check.CheckPath!),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Assert.NotNull(publication);
                Assert.True(publication.Valid);
                Assert.Equal(
                    new[] { alphaTarget, betaTarget }.Order(StringComparer.Ordinal),
                    publication.Files.Select(file => file.TargetPath).Order(StringComparer.Ordinal));

                await check.DisposeAsync();
                check = null;
                Assert.False(proof.Validate());
            }

            Assert.Single(service.Commands, command => command.Label == "docker nginx writer identity");
            Assert.Single(service.Commands, command => command.Label == "docker nginx writer ownership");
        }
        finally
        {
            if (check is not null)
            {
                await check.DisposeAsync();
            }
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_HostCoverageStaysWithItsSelectedPathAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-host-coverage-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        var alphaTarget = Path.Combine(alpha, "access.log");
        var betaTarget = Path.Combine(beta, "access.log");
        await File.WriteAllTextAsync(alphaTarget, "alpha");
        await File.WriteAllTextAsync(betaTarget, "beta");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = false, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "host nginx writer identity" when command.ArgumentList.Contains(alphaTarget) =>
                new ProcessCommandResult { ExitCode = 0, Output = "501|alpha-start\n" },
            "host nginx writer identity" when command.ArgumentList.Contains(betaTarget) =>
                new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var sources = new[]
        {
            CreateDatasource("alpha", root, alpha, alphaTarget),
            CreateDatasource("beta", root, beta, betaTarget)
        };
        NginxHeldProof? proof = null;
        try
        {
            await using (var check = await service.PrepareReopenCheckAsync(
                sources,
                new[] { alphaTarget, betaTarget },
                expectsPublication: false))
            {
                Assert.Equal("host", Assert.Single(check.Writers).Name);
                proof = Assert.Single(check.Proofs);
                Assert.Equal(betaTarget, proof.Path);
                Assert.Equal(2, check.OriginalIdentities.Count);
                service.ValidateReopenCheck(check);
            }

            Assert.NotNull(proof);
            Assert.False(proof.Validate());
            var probes = service.Commands.Where(command => command.Label == "host nginx writer identity").ToList();
            Assert.Equal(2, probes.Count);
            Assert.All(probes, command => Assert.Equal(6, command.ArgumentList.Length));
            Assert.Single(probes, command => command.ArgumentList.Contains(alphaTarget));
            Assert.Single(probes, command => command.ArgumentList.Contains(betaTarget));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_SeparateFilesRetainSeparateWritersAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-separate-writers-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        var alphaTarget = Path.Combine(alpha, "access.log");
        var betaTarget = Path.Combine(beta, "access.log");
        await File.WriteAllTextAsync(alphaTarget, "alpha");
        await File.WriteAllTextAsync(betaTarget, "beta");
        var alphaIdentity = NginxWriterProbe.ReadIdentity(alphaTarget);
        var betaIdentity = NginxWriterProbe.ReadIdentity(betaTarget);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "runtime-manager\nwriter-a\nwriter-b\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[202]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"/host/alpha|{alpha}\n/host/beta|{beta}\n"
                },
            "docker nginx mount inspection" when command.Arguments.Contains("writer-a", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = "/host/alpha|/data/alpha\n" },
            "docker nginx mount inspection" =>
                new ProcessCommandResult { ExitCode = 0, Output = "/host/beta|/data/beta\n" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[202]\n" },
            "docker nginx writer identity" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 1 },
            "docker nginx writer identity" when command.Arguments.Contains("writer-a", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = "71|alpha-start\n" },
            "docker nginx writer identity" =>
                new ProcessCommandResult { ExitCode = 0, Output = "72|beta-start\n" },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "docker nginx file identity" when command.ArgumentList[^1] == "/data/alpha/access.log" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{alphaIdentity.First}|{alphaIdentity.Second}\n"
                },
            "docker nginx file identity" =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{betaIdentity.First}|{betaIdentity.Second}\n"
                },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            "docker nginx verified reopen" => new ProcessCommandResult { ExitCode = 0 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var sources = new[]
        {
            CreateDatasource("alpha", root, alpha, alphaTarget),
            CreateDatasource("beta", root, beta, betaTarget)
        };
        try
        {
            await using var check = await service.PrepareReopenCheckAsync(
                sources,
                new[] { alphaTarget, betaTarget },
                expectsPublication: false);

            Assert.Equal(new[] { "writer-a", "writer-b" }, check.Writers.Select(writer => writer.Name));
            Assert.Empty(check.Proofs);
            Assert.Equal(2, check.OriginalIdentities.Count);
            var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);
            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(2, service.Commands.Count(command => command.Label == "docker nginx writer ownership"));
            Assert.Equal(2, service.Commands.Count(command => command.Label == "docker nginx verified reopen"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_DisappearingUncoveredFileRejectsMixedPreparationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-missing-proof-" + Guid.NewGuid().ToString("N"));
        var alpha = Path.Combine(root, "alpha");
        var beta = Path.Combine(root, "beta");
        Directory.CreateDirectory(alpha);
        Directory.CreateDirectory(beta);
        var alphaTarget = Path.Combine(alpha, "access.log");
        var betaTarget = Path.Combine(beta, "access.log");
        await File.WriteAllTextAsync(alphaTarget, "alpha");
        await File.WriteAllTextAsync(betaTarget, "beta");
        var alphaIdentity = NginxWriterProbe.ReadIdentity(alphaTarget);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "writer"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" =>
                new ProcessCommandResult { ExitCode = 0, Output = "runtime-manager\nwriter\n" },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[203]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/host/alpha|{alpha}\n" },
            "docker nginx mount inspection" =>
                new ProcessCommandResult { ExitCode = 0, Output = "/host/alpha|/data/alpha\n" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[203]\n" },
            "docker nginx writer identity" =>
                new ProcessCommandResult { ExitCode = 0, Output = "71|alpha-start\n" },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "docker nginx file identity" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"{alphaIdentity.First}|{alphaIdentity.Second}\n"
            },
            "host nginx writer identity" when command.ArgumentList.Contains(betaTarget) => DeleteBeta(),
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var sources = new[]
        {
            CreateDatasource("alpha", root, alpha, alphaTarget),
            CreateDatasource("beta", root, beta, betaTarget)
        };
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.PrepareReopenCheckAsync(
                    sources,
                    new[] { alphaTarget, betaTarget },
                    expectsPublication: true));

            Assert.Contains(Path.GetFileName(betaTarget), error.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(root, "nginx_log_check_*.json", SearchOption.AllDirectories));
            Assert.DoesNotContain(service.Commands, command => command.Label == "docker nginx verified reopen");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        ProcessCommandResult DeleteBeta()
        {
            File.Delete(betaTarget);
            return new ProcessCommandResult { ExitCode = 1 };
        }
    }

    [Theory]
    [InlineData(0, "", "")]
    [InlineData(0, "not-a-namespace", "")]
    [InlineData(9, "", "namespace denied")]
    public async Task PrepareReopenCheckAsync_InvalidManagerNamespaceDoesNotClaimNoWriterAsync(
        int exitCode,
        string namespaceValue,
        string errorText)
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-namespace-failure-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult { ExitCode = 0, Output = "runtime-manager\n" },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[104]\n" },
            "docker nginx mount inspection" =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/fixture/logs|{logs}\n" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = exitCode, Output = namespaceValue, Error = errorText },
            "docker nginx file identity" => new ProcessCommandResult { ExitCode = 0, Output = "0|0\n" },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareReopenCheckAsync(new[] { source }, new[] { target }, expectsPublication: true));

        Assert.Contains("docker manager mount namespace", error.Message, StringComparison.Ordinal);
        Assert.Contains($"exit code {exitCode}", error.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(errorText))
        {
            Assert.Contains(errorText, error.Message, StringComparison.Ordinal);
        }
        Directory.Delete(root, recursive: true);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(0, "not-an-identity")]
    [InlineData(11, "")]
    public async Task PrepareReopenCheckAsync_InvalidWriterFileIdentityDoesNotClaimNoWriterAsync(
        int exitCode,
        string identityValue)
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-file-identity-failure-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "runtime-manager\nwriter\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[105]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/fixture/logs|{logs}\n" },
            "docker nginx mount inspection" =>
                new ProcessCommandResult { ExitCode = 0, Output = "/fixture/logs|/data/logs\n" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[105]\n" },
            "docker nginx file identity" when command.ArgumentList.Contains("runtime-manager") =>
                new ProcessCommandResult { ExitCode = 0, Output = "0|0\n" },
            "docker nginx file identity" => new ProcessCommandResult
            {
                ExitCode = exitCode,
                Output = identityValue,
                Error = exitCode == 0 ? string.Empty : "stat denied"
            },
            "docker nginx writer identity" when command.Arguments.Contains("writer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = "72|owned\n" },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 0 },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareReopenCheckAsync(new[] { source }, new[] { target }, expectsPublication: true));

        Assert.Contains("docker nginx file identity", error.Message, StringComparison.Ordinal);
        Assert.Contains($"exit code {exitCode}", error.Message, StringComparison.Ordinal);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_ForeignSameDestinationDoesNotSelectWriterAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-foreign-file-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var identity = NginxWriterProbe.ReadIdentity(target);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "runtime-manager\nwriter\n"
            },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[106]\n" },
            "docker nginx mount inspection" when command.Arguments.Contains("runtime-manager", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = $"/host/right|{logs}\n" },
            "docker nginx mount inspection" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"/host/foreign|{logs}\n/host/right-old|/data/logs-old\n"
            },
            "docker manager mount namespace" when command.ArgumentList.Contains("runtime-manager") =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[106]\n" },
            "docker manager mount namespace" =>
                new ProcessCommandResult { ExitCode = 0, Output = "mnt:[206]\n" },
            "docker nginx file identity" when command.ArgumentList.Contains("runtime-manager") =>
                new ProcessCommandResult
                {
                    ExitCode = 0,
                    Output = $"{identity.First}|{identity.Second}\n"
                },
            "docker nginx file identity" => new ProcessCommandResult { ExitCode = 0, Output = "0|0\n" },
            "docker nginx writer identity" when command.Arguments.Contains("writer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0, Output = "72|owned\n" },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 0, Output = "71|foreign\n" },
            "docker nginx writer ownership" when command.Arguments.Contains("writer", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0 },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 4 },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        await using (var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: false))
        {
            Assert.Empty(check.Writers);
            Assert.Equal(NginxReopenRequirement.NotRequired, check.Requirement);
        }
        Assert.DoesNotContain(service.Commands, command => command.Label == "docker nginx verified reopen");
        Assert.DoesNotContain(service.Commands, command =>
            command.Label == "docker nginx file identity" &&
            command.ArgumentList[^1].StartsWith("/data/logs-old", StringComparison.Ordinal));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_FailedWriterListDoesNotClaimNoWriterAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-writer-list-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "auto"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" =>
                new ProcessCommandResult { ExitCode = 12, Error = "list denied" },
            "host mount namespace" => new ProcessCommandResult { ExitCode = 0, Output = "mnt:[107]\n" },
            "host nginx writer identity" => new ProcessCommandResult { ExitCode = 1 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, root, target);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareReopenCheckAsync(new[] { source }, new[] { target }, expectsPublication: true));

        Assert.Contains("docker nginx writer list", error.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 12", error.Message, StringComparison.Ordinal);
        Assert.Contains("list denied", error.Message, StringComparison.Ordinal);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_HostWriterPassesSelectedIdentityArgumentsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-host-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var identity = NginxWriterProbe.ReadIdentity(target);
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = false, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = true,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command =>
        {
            Assert.Equal("host nginx writer identity", command.Label);
            Assert.Contains("/proc/$pid/root", command.ArgumentList[1], StringComparison.Ordinal);
            Assert.DoesNotContain("readlink -f", command.ArgumentList[1], StringComparison.Ordinal);
            Assert.Equal(target, command.ArgumentList[^3]);
            Assert.Equal(identity.First.ToString(), command.ArgumentList[^2]);
            Assert.Equal(identity.Second.ToString(), command.ArgumentList[^1]);
            return new ProcessCommandResult { ExitCode = 0, Output = "81|host-owned\n" };
        };
        var source = CreateDatasource("default", root, root, target);

        await using (var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: false))
        {
            var writer = Assert.Single(check.Writers);
            Assert.Equal(NginxWriterKind.Host, writer.Kind);
            Assert.Equal(81, writer.ProcessId);
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_ForeignCandidateDoesNotHideOwnedCandidateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-candidates-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "writer"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = false,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult { ExitCode = 0, Output = "writer\n" },
            "docker nginx mount inspection" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"{logs}|/logs\n"
            },
            "docker nginx writer identity" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = "71|foreign\n72|owned\n"
            },
            "docker nginx writer ownership" when command.Arguments.Contains("/proc/71/", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 4 },
            "docker nginx writer ownership" when command.Arguments.Contains("/proc/72/", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0 },
            "docker nginx verified reopen" when command.Arguments.Contains("kill -USR1 72", StringComparison.Ordinal) =>
                new ProcessCommandResult { ExitCode = 0 },
            "docker nginx verified reopen" => new ProcessCommandResult { ExitCode = 23, Error = "foreign writer" },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        await using (var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: false))
        {
            var result = await service.CompleteReopenCheckAsync(check, physicalChange: true);

            Assert.Equal(72, Assert.Single(check.Writers).ProcessId);
            Assert.True(result.Success, result.ErrorMessage);
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task PrepareReopenCheckAsync_ConfiguredForeignObserverFailsBeforePublicationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "nginx-explicit-observer-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllTextAsync(target, "line");
        var service = new TestNginxLogRotationService(
            NullLogger<NginxLogRotationService>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NginxLogRotation:ContainerName"] = "observer"
            }).Build(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            new TestPathResolver(NullLogger.Instance) { DockerSocketAvailable = true, Root = root },
            TimeProvider.System)
        {
            ProbeHostWriters = false,
            ReplaceDockerLogs = true
        };
        service.OnCommand = command => command.Label switch
        {
            "docker nginx writer list" => new ProcessCommandResult { ExitCode = 0, Output = "observer\n" },
            "docker nginx mount inspection" => new ProcessCommandResult
            {
                ExitCode = 0,
                Output = $"{logs}|/logs\n"
            },
            "docker nginx writer identity" => new ProcessCommandResult { ExitCode = 0, Output = "71|foreign\n" },
            "docker nginx writer ownership" => new ProcessCommandResult { ExitCode = 4 },
            _ => throw new InvalidOperationException($"Unexpected command: {command.Label} {command.Arguments}")
        };
        var source = CreateDatasource("default", root, logs, target);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PrepareReopenCheckAsync(new[] { source }, new[] { target }, expectsPublication: true));

        Assert.Contains("could not be verified", error.Message, StringComparison.Ordinal);
        var operations = Path.Combine(root, "operations");
        Assert.True(!Directory.Exists(operations) ||
            !Directory.EnumerateFiles(operations, "nginx_log_*").Any());
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task CompleteReopenCheckAsync_RequiredZeroChangeSignalsWriterAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        await using var check = CreateRequiredCheck("writer");

        var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(NginxReopenStatus.Succeeded, result.Status);
        Assert.Equal(NginxReopenRequirement.Required, result.Requirement);
        Assert.False(result.PartialPhysicalEffects);
        Assert.Equal("docker nginx verified reopen", Assert.Single(service.Commands).Label);
    }

    [Fact]
    public async Task CompleteReopenCheckAsync_RequiredZeroChangeFailureRetainsProcessDetailsAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.ProcessResults.Enqueue(new ProcessCommandResult
        {
            ExitCode = 17,
            Error = "  kill: Operation not permitted  \n"
        });
        await using var check = CreateRequiredCheck("writer");

        var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

        Assert.False(result.Success);
        Assert.Equal(NginxReopenStatus.Failed, result.Status);
        Assert.Equal(NginxReopenRequirement.Required, result.Requirement);
        Assert.False(result.PartialPhysicalEffects);
        Assert.Contains("writer", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("exit code 17", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("kill: Operation not permitted", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteReopenCheckAsync_NoWriterZeroChangeRemainsNotRequiredAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        await using var check = new NginxReopenCheck(
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, NginxFileIdentity>(),
            Array.Empty<NginxWriterIdentity>(),
            NginxReopenRequirement.NotRequired,
            Array.Empty<NginxHeldProof>(),
            null,
            null,
            expectsPublication: false);

        var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

        Assert.True(result.Success);
        Assert.Equal(NginxReopenStatus.NotRequired, result.Status);
        Assert.Equal(NginxReopenRequirement.NotRequired, result.Requirement);
        Assert.False(result.PartialPhysicalEffects);
        Assert.Empty(service.Commands);
    }

    [Fact]
    public async Task CompleteReopenCheckAsync_HungWriterSignalFailsAndFreesTheLogLockAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-nginx-hung-signal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false))
        {
            var service = CreateService(new CapturingLogger<NginxLogRotationService>());
            service.ReopenNeverReturns = true;
            await using var check = CreateRequiredCheck("writer");
            using var caller = new CancellationTokenSource();

            LogRotationResult result;
            await using (await harness.Owner.LockLogFilesAsync(
                null,
                OperationType.LogRemoval,
                LogFileLockKind.Rewrite,
                caller.Token))
            {
                result = await service.CompleteReopenCheckAsync(check, physicalChange: true, caller.Token)
                    .WaitAsync(TimeSpan.FromSeconds(45));
            }

            Assert.False(result.Success);
            Assert.Equal(NginxReopenStatus.Failed, result.Status);
            Assert.Equal(NginxReopenRequirement.Required, result.Requirement);
            Assert.True(result.PartialPhysicalEffects);
            Assert.Contains("writer", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal("docker nginx verified reopen", Assert.Single(service.Commands).Label);
            await harness.Owner.WaitForLogStepAsync(active: false, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ScheduledRotationWaitsForTheLogStepBeforeItReopensNginxAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-nginx-rotation-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false))
        {
            var service = CreateService(new CapturingLogger<NginxLogRotationService>());
            service.DetectionResult = (null, "No container with nginx found");
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "42|1234\n" });
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
            var rotation = new NginxLogRotationHostedService(
                service,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                NullLogger<NginxLogRotationHostedService>.Instance,
                new TestPathResolver(NullLogger.Instance),
                harness.StateService,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                harness.Tracker,
                harness.Owner);
            var executeWork = typeof(NginxLogRotationHostedService).GetMethod(
                "ExecuteWorkAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Task run;
            await using (await harness.Owner.LockLogFilesAsync(
                null,
                OperationType.LogRemoval,
                LogFileLockKind.Rewrite,
                CancellationToken.None))
            {
                run = (Task)executeWork.Invoke(rotation, [CancellationToken.None])!;
                // While a log step holds the lock, the rotation waits and signals nothing.
                Assert.NotSame(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(2))));
                Assert.Empty(service.Commands);
            }

            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(
                new[] { "host nginx writer identity", "host nginx verified reopen" },
                service.Commands.Select(command => command.Label));
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ScheduledRotationRunsWhileAnImportPassAndTheSpeedTrackerRunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-nginx-rotation-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false))
        {
            var service = CreateService(new CapturingLogger<NginxLogRotationService>());
            service.DetectionResult = (null, "No container with nginx found");
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "42|1234\n" });
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
            var rotation = new NginxLogRotationHostedService(
                service,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                NullLogger<NginxLogRotationHostedService>.Instance,
                new TestPathResolver(NullLogger.Instance),
                harness.StateService,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                harness.Tracker,
                harness.Owner);
            var executeWork = typeof(NginxLogRotationHostedService).GetMethod(
                "ExecuteWorkAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            Task run;
            await using (await harness.Owner.LockLogFilesAsync(
                null,
                OperationType.LogProcessing,
                LogFileLockKind.Ingest,
                CancellationToken.None))
            {
                Assert.True(harness.Owner.TryBeginSpeedTrackerRun());
                run = (Task)executeWork.Invoke(rotation, [CancellationToken.None])!;
                await run.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(
                new[] { "host nginx writer identity", "host nginx verified reopen" },
                service.Commands.Select(command => command.Label));
            harness.Owner.EndSpeedTrackerRun();
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ACancelWhileTheRotationWaitsForALogStepEndsItCanceledWithoutSignalingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-nginx-rotation-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false))
        {
            var service = CreateService(new CapturingLogger<NginxLogRotationService>());
            var rotation = new NginxLogRotationHostedService(
                service,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                NullLogger<NginxLogRotationHostedService>.Instance,
                new TestPathResolver(NullLogger.Instance),
                harness.StateService,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                harness.Tracker,
                harness.Owner);
            var executeWork = typeof(NginxLogRotationHostedService).GetMethod(
                "ExecuteWorkAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            await using (await harness.Owner.LockLogFilesAsync(
                null,
                OperationType.LogRemoval,
                LogFileLockKind.Rewrite,
                CancellationToken.None))
            {
                var run = (Task)executeWork.Invoke(rotation, [CancellationToken.None])!;
                OperationInfo? operation = null;
                for (var attempt = 0; attempt < 200 && operation is null; attempt++)
                {
                    operation = harness.Tracker.GetActiveOperations(OperationType.LogRotation).FirstOrDefault();
                    if (operation is null)
                    {
                        await Task.Delay(25);
                    }
                }
                Assert.NotNull(operation);

                harness.Tracker.CancelOperation(operation.Id);
                await run.WaitAsync(TimeSpan.FromSeconds(10));

                Assert.Empty(service.Commands);
                Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(operation.Id)!.Status);
            }
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task AHungDockerCommandFailsTheScheduledRotationAfterItsLimitAndFreesTheLogLockAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-nginx-rotation-hung-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false))
        {
            var service = CreateService(new CapturingLogger<NginxLogRotationService>());
            service.DetectionResult = ("lancache", null);
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "42|1234\n" });
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
            service.ReopenNeverReturns = true;
            var rotation = new NginxLogRotationHostedService(
                service,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                NullLogger<NginxLogRotationHostedService>.Instance,
                new TestPathResolver(NullLogger.Instance),
                harness.StateService,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                harness.Tracker,
                harness.Owner);
            var executeWork = typeof(NginxLogRotationHostedService).GetMethod(
                "ExecuteWorkAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var run = (Task)executeWork.Invoke(rotation, [CancellationToken.None])!;
            OperationInfo? operation = null;
            for (var attempt = 0; attempt < 200 && operation is null; attempt++)
            {
                operation = harness.Tracker.GetActiveOperations(OperationType.LogRotation).FirstOrDefault();
                if (operation is null)
                {
                    await Task.Delay(25);
                }
            }
            Assert.NotNull(operation);

            // It waits the real 30 seconds, as the log step's own signal limit test does.
            await run.WaitAsync(TimeSpan.FromSeconds(45));

            var ended = harness.Tracker.GetOperation(operation.Id)!;
            Assert.Equal(OperationStatus.Failed, ended.Status);
            Assert.Contains("30 seconds", ended.Message, StringComparison.Ordinal);
            await using (await harness.Owner.LockLogFilesAsync(
                null,
                OperationType.LogRemoval,
                LogFileLockKind.Rewrite,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)))
            {
            }
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task XOnTheRotationCardStopsAHungReopenAndEndsItCanceledAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-nginx-rotation-hung-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using (var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, start: false))
        {
            var service = CreateService(new CapturingLogger<NginxLogRotationService>());
            service.DetectionResult = ("lancache", null);
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "42|1234\n" });
            service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
            service.ReopenNeverReturns = true;
            var rotation = new NginxLogRotationHostedService(
                service,
                new ConfigurationBuilder().AddInMemoryCollection().Build(),
                NullLogger<NginxLogRotationHostedService>.Instance,
                new TestPathResolver(NullLogger.Instance),
                harness.StateService,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                harness.Tracker,
                harness.Owner);
            var executeWork = typeof(NginxLogRotationHostedService).GetMethod(
                "ExecuteWorkAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var run = (Task)executeWork.Invoke(rotation, [CancellationToken.None])!;
            OperationInfo? operation = null;
            for (var attempt = 0; attempt < 200 && operation is null; attempt++)
            {
                operation = harness.Tracker.GetActiveOperations(OperationType.LogRotation).FirstOrDefault();
                if (operation is null)
                {
                    await Task.Delay(25);
                }
            }
            Assert.NotNull(operation);
            for (var attempt = 0;
                 attempt < 200 && !service.Commands.Any(command => command.Label == "docker nginx verified reopen");
                 attempt++)
            {
                await Task.Delay(25);
            }
            Assert.Contains(service.Commands, command => command.Label == "docker nginx verified reopen");

            harness.Tracker.CancelOperation(operation.Id);
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(OperationStatus.Cancelled, harness.Tracker.GetOperation(operation.Id)!.Status);
            await using (await harness.Owner.LockLogFilesAsync(
                null,
                OperationType.LogRemoval,
                LogFileLockKind.Rewrite,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)))
            {
            }
        }
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ReopenNginxLogsAsync_NoContainer_SignalsHostWithExpectedCommandAsync()
    {
        var logger = new CapturingLogger<NginxLogRotationService>();
        var service = CreateService(logger);
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "42|1234\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var result = await service.ReopenNginxLogsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Collection(
            service.Commands,
            identity =>
            {
                Assert.Equal("host nginx writer identity", identity.Label);
                Assert.Equal("sh", identity.FileName);
                Assert.Contains("nginx[:] master", identity.ArgumentList[1], StringComparison.Ordinal);
            },
            signal =>
            {
                Assert.Equal("host nginx verified reopen", signal.Label);
                Assert.Equal("sh", signal.FileName);
                Assert.Contains("/proc/42/stat", signal.ArgumentList[1], StringComparison.Ordinal);
                Assert.Contains("kill -USR1 42", signal.ArgumentList[1], StringComparison.Ordinal);
            });
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HostProbeAndSignalCommands_UseSelfMatchProofNginxPatternAsync()
    {
        var service = CreateService(new CapturingLogger<NginxLogRotationService>());
        service.DetectionResult = (null, "No container with nginx found");
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "42|1234\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var available = await service.CanReopenNginxAsync();
        var reopenResult = await service.ReopenNginxLogsAsync(CancellationToken.None);

        Assert.True(available);
        Assert.True(reopenResult.Success);
        Assert.Equal(3, service.Commands.Count);
        foreach (var invocation in service.Commands.Take(2))
        {
            Assert.Equal("sh", invocation.FileName);
            Assert.True(invocation.ArgumentList.Length >= 2);
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

        var result = await service.ReopenNginxLogsAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.DockerSocketMissing);
        Assert.Contains("pid: host", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("CAP_KILL", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("code 3", result.ErrorMessage, StringComparison.Ordinal);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("pid: host", warning.Message, StringComparison.Ordinal);
        Assert.Contains("root is not required", warning.Message, StringComparison.Ordinal);
        Assert.Contains("CAP_KILL", warning.Message, StringComparison.Ordinal);
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

        var first = await service.ReopenNginxLogsAsync(CancellationToken.None);
        var second = await service.ReopenNginxLogsAsync(CancellationToken.None);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.True(first.DockerSocketMissing);
        Assert.Contains("Failed to reopen nginx writer", first.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        var third = await service.ReopenNginxLogsAsync(CancellationToken.None);

        Assert.False(third.Success);
        Assert.Equal(6, service.Commands.Count);
    }

    [Fact]
    public async Task ReopenNginxLogsAsync_ContainerFound_KeepsDockerSignalPathAsync()
    {
        var logger = new CapturingLogger<NginxLogRotationService>();
        var service = CreateService(logger);
        service.DetectionResult = ("lancache-monolithic", null);
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0, Output = "1|5678\n" });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });
        service.ProcessResults.Enqueue(new ProcessCommandResult { ExitCode = 0 });

        var result = await service.ReopenNginxLogsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, service.DetectionCalls);
        Assert.Collection(
            service.Commands,
            invocation =>
            {
                Assert.Equal("docker nginx writer identity", invocation.Label);
                Assert.Equal("docker", invocation.FileName);
                Assert.Contains("exec lancache-monolithic sh -c", invocation.Arguments, StringComparison.Ordinal);
                Assert.Empty(invocation.ArgumentList);
            },
            invocation =>
            {
                Assert.Equal("docker nginx writer ownership", invocation.Label);
                Assert.Equal("docker", invocation.FileName);
                Assert.Contains("/proc/1/ns/mnt", invocation.Arguments, StringComparison.Ordinal);
                Assert.Empty(invocation.ArgumentList);
            },
            invocation =>
            {
                Assert.Equal("docker nginx verified reopen", invocation.Label);
                Assert.Equal("docker", invocation.FileName);
                Assert.Contains("/proc/1/stat", invocation.Arguments, StringComparison.Ordinal);
                Assert.Contains("kill -USR1 1", invocation.Arguments, StringComparison.Ordinal);
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
                ExitCode = 0,
                Output = "42|1234\n"
            });
            service.ProcessResults.Enqueue(new ProcessCommandResult
            {
                ExitCode = 1,
                Error = "kill: Operation not permitted"
            });
        }
    }

    private static ResolvedDatasource CreateDatasource(
        string name,
        string cache,
        string logs,
        string target) => new()
        {
            Name = name,
            CachePath = cache,
            ConfiguredLogPath = logs,
            LogPath = logs,
            LogFilePath = target,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };

    private static NginxReopenCheck CreateRequiredCheck(string writer) => new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        new Dictionary<string, NginxFileIdentity>(),
        new[] { new NginxWriterIdentity(NginxWriterKind.Docker, writer, 71, "start") },
        NginxReopenRequirement.Required,
        Array.Empty<NginxHeldProof>(),
        null,
        null,
        expectsPublication: false);

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
        public Func<CommandInvocation, ProcessCommandResult>? OnCommand { get; set; }
        public bool ProbeHostWriters { get; set; } = true;
        public bool ReplaceDockerLogs { get; set; } = true;
        public bool ReopenNeverReturns { get; set; }
        protected override bool CanProbeHostWriters => ProbeHostWriters;
        protected override bool CanReplaceDockerLogs => ReplaceDockerLogs;

        protected override Task<(string? ContainerName, string? Error)> FindMonolithicContainerAsync(CancellationToken cancellationToken)
        {
            DetectionCalls++;
            return Task.FromResult(DetectionResult);
        }

        protected override Task<ProcessCommandResult> RunProcessAsync(
            ProcessStartInfo startInfo,
            string label,
            CancellationToken cancellationToken = default)
        {
            var command = new CommandInvocation(
                label,
                startInfo.FileName,
                startInfo.Arguments,
                startInfo.ArgumentList.ToArray(),
                startInfo.RedirectStandardOutput,
                startInfo.RedirectStandardError,
                startInfo.UseShellExecute);
            Commands.Add(command);
            if (ReopenNeverReturns && label == "docker nginx verified reopen")
            {
                return new TaskCompletionSource<ProcessCommandResult>().Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(OnCommand?.Invoke(command) ?? ProcessResults.Dequeue());
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
            string label,
            CancellationToken cancellationToken = default)
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
        protected override string BasePath => Root;
        protected override string RustExecutableExtension => string.Empty;
        public bool DockerSocketAvailable { get; init; }
        public string Root { get; init; } = "/test";

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
