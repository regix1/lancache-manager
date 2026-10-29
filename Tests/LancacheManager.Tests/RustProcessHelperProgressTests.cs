using System.Collections.Concurrent;
using System.Diagnostics;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class RustProcessHelperProgressTests
{
    [Fact]
    public async Task StalePollCallbackDrainsBeforeTerminalProgressIsDeliveredAsync()
    {
        using var pollCts = new CancellationTokenSource();
        var progressPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(progressPath, """{"status":"completed"}""");
        var callbacks = new ConcurrentQueue<string>();
        var callbackStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pollTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, pollCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Model a poll that already read an older snapshot and entered its callback.
            }

            callbackStarted.SetResult();
            await releaseCallback.Task;
            callbacks.Enqueue("stale");
        });

        try
        {
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager: null!,
                pathResolver: null!,
                operationTracker: null!);
            var delivering = helper.StopProgressPollingAndDeliverTerminalProgressAsync<ProgressState>(
                pollCts,
                pollTask,
                progressPath,
                progress =>
                {
                    callbacks.Enqueue(progress.Status);
                    return Task.CompletedTask;
                });
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(delivering.IsCompleted);
            releaseCallback.SetResult();
            await delivering.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(["stale", "completed"], callbacks);
        }
        finally
        {
            File.Delete(progressPath);
        }
    }

    [Fact]
    public async Task ATrackedChildThatDiesLeavesTheParentAbleToReportItsExitAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script;
            if (OperatingSystem.IsWindows())
            {
                script = Path.Combine(root, "child.cmd");
                File.WriteAllText(script, "@echo off\r\nexit /b 137\r\n");
            }
            else
            {
                script = Path.Combine(root, "child.sh");
                File.WriteAllText(script, "#!/bin/sh\nexit 137\n");
                File.SetUnixFileMode(
                    script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager,
                pathResolver: null!,
                operationTracker: null!);
            var launch = helper.CreateProcessStartInfo(script, string.Empty);
            var exitCode = await helper.RunTrackedProcessAsync(
                launch,
                operationId: null,
                CancellationToken.None,
                async process =>
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode;
                },
                "test");

            Assert.Equal(137, exitCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ATrackedChildOnLinuxGetsTheHighestOutOfMemoryScoreAsync()
    {
        // The score file only exists under /proc on Linux; elsewhere there is nothing to read.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "child.sh");
            File.WriteAllText(script, "#!/bin/sh\nwhile true; do sleep 1; done\n");
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager,
                pathResolver: null!,
                operationTracker: null!);
            var launch = helper.CreateProcessStartInfo(script, string.Empty);
            var score = await helper.RunTrackedProcessAsync(
                launch,
                operationId: null,
                CancellationToken.None,
                async process =>
                {
                    var read = await File.ReadAllTextAsync($"/proc/{process.Id}/oom_score_adj");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    return read.Trim();
                },
                "test");

            Assert.Equal("1000", score);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ATrackedChildOnLinuxTurnsOffGroupOutOfMemoryKillsAsync()
    {
        // The helper changes the switch only on Linux, where the container runtime owns it.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "child.sh");
            File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var groupSwitch = Path.Combine(root, "memory.oom.group");
            File.WriteAllText(groupSwitch, "1\n");

            using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager,
                pathResolver: null!,
                operationTracker: null!)
            {
                OomGroupPath = groupSwitch
            };
            await helper.RunTrackedProcessAsync(
                helper.CreateProcessStartInfo(script, string.Empty),
                operationId: null,
                CancellationToken.None,
                async process =>
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode;
                },
                "test");

            Assert.Equal("0", File.ReadAllText(groupSwitch).Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DroppingProgressLinesKeepsOnlyTheResultLineAsync()
    {
        const string firstEvent = """{"event":"progress","operationId":"scan","percentComplete":10,"status":"running"}""";
        const string secondEvent = """{"event":"progress","operationId":"scan","percentComplete":60,"status":"running"}""";
        const string resultLine = """{"success":true,"processed":5,"evicted":1,"unEvicted":0,"filesOnDisk":9}""";
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script;
            if (OperatingSystem.IsWindows())
            {
                script = Path.Combine(root, "child.cmd");
                File.WriteAllText(
                    script,
                    $"@echo off\r\necho {firstEvent}\r\necho {secondEvent}\r\necho {resultLine}\r\nexit /b 0\r\n");
            }
            else
            {
                script = Path.Combine(root, "child.sh");
                File.WriteAllText(
                    script,
                    $"#!/bin/sh\nprintf '%s\\n' '{firstEvent}' '{secondEvent}' '{resultLine}'\n");
                File.SetUnixFileMode(
                    script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager,
                pathResolver: null!,
                operationTracker: null!);
            var delivered = new ConcurrentQueue<double?>();
            RustProcessHelper.DropProgressStdout.Value = true;
            ProcessExecutionResult result;
            try
            {
                result = await helper.ExecuteTrackedProcessWithProgressEventsAsync(
                    helper.CreateProcessStartInfo(script, string.Empty),
                    operationId: null,
                    CancellationToken.None,
                    progressEvent =>
                    {
                        delivered.Enqueue(progressEvent.PercentComplete);
                        return Task.CompletedTask;
                    },
                    "test");
            }
            finally
            {
                RustProcessHelper.DropProgressStdout.Value = false;
            }

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(resultLine, result.Output);
            Assert.Equal([10d, 60d], delivered);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DroppingProgressLinesKeepsTheResultLinePrintedBeforeAFailedEventAsync()
    {
        // A scan that fails on its own prints its result, then its failed event, then exits 1.
        const string resultLine = """{"success":false,"processed":0,"evicted":0,"unEvicted":0,"filesOnDisk":0,"error":"database connection lost"}""";
        const string failedEvent = """{"event":"failed","operationId":"scan","percentComplete":100,"status":"failed"}""";
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string script;
            if (OperatingSystem.IsWindows())
            {
                script = Path.Combine(root, "child.cmd");
                File.WriteAllText(
                    script,
                    $"@echo off\r\necho {resultLine}\r\necho {failedEvent}\r\nexit /b 1\r\n");
            }
            else
            {
                script = Path.Combine(root, "child.sh");
                File.WriteAllText(
                    script,
                    $"#!/bin/sh\nprintf '%s\\n' '{resultLine}' '{failedEvent}'\nexit 1\n");
                File.SetUnixFileMode(
                    script,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager,
                pathResolver: null!,
                operationTracker: null!);
            RustProcessHelper.DropProgressStdout.Value = true;
            ProcessExecutionResult result;
            try
            {
                result = await helper.ExecuteTrackedProcessWithProgressEventsAsync(
                    helper.CreateProcessStartInfo(script, string.Empty),
                    operationId: null,
                    CancellationToken.None,
                    _ => Task.CompletedTask,
                    "test");
            }
            finally
            {
                RustProcessHelper.DropProgressStdout.Value = false;
            }

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(resultLine, result.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record ProgressState(string Status);
}
