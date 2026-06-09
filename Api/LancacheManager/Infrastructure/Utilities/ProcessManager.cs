using System.Collections.Concurrent;
using System.Diagnostics;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Central process lifecycle: track spawned processes for app shutdown, kill process trees on cancel,
/// and run short-lived commands with consistent wait/output handling.
/// </summary>
public class ProcessManager : IHostedService, IDisposable
{
    private readonly ILogger<ProcessManager> _logger;
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();

    public ProcessManager(ILogger<ProcessManager> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ProcessManager started - will track and cleanup spawned processes");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogWarning("ProcessManager stopping - terminating {Count} active processes", _activeProcesses.Count);

        var killTasks = _activeProcesses.Values.Select(process => Task.Run(async () =>
        {
            try
            {
                KillProcessTree(process, "application shutdown", log: false);
                await WaitForExitAfterKillAsync(process, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error terminating process {ProcessId}", process.Id);
            }
        }));

        await Task.WhenAll(killTasks);
        _activeProcesses.Clear();
    }

    /// <summary>
    /// Registers a process so it is terminated during application shutdown.
    /// </summary>
    public void Track(Process process) => _activeProcesses.TryAdd(process.Id, process);

    /// <summary>
    /// Removes a process from shutdown tracking once it has exited.
    /// </summary>
    public void Untrack(Process process) => _activeProcesses.TryRemove(process.Id, out _);

    /// <summary>
    /// Kills a process and its child processes. Safe to call when already exited or handle is stale.
    /// </summary>
    public bool KillProcessTree(Process process, string reason, bool log = true)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            if (log)
            {
                _logger.LogWarning(
                    "Killing process tree {ProcessName} (PID: {ProcessId}): {Reason}",
                    process.ProcessName,
                    process.Id,
                    reason);
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ObjectDisposedException ex)
        {
            _logger.LogDebug(ex, "Process handle already disposed while killing process");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Process handle invalid while killing PID {ProcessId}", process.Id);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill process tree PID {ProcessId}", process.Id);
            return false;
        }
    }

    /// <summary>
    /// Waits for a process to exit. Does not kill on cancellation — callers must kill separately.
    /// </summary>
    public Task WaitForExitAsync(Process process, CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// Waits for a process to exit after a kill signal, with timeout.
    /// </summary>
    public async Task WaitForExitAfterKillAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout);
            _logger.LogInformation("Process {ProcessId} terminated successfully", process.Id);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Process {ProcessId} did not exit within {Seconds}s after kill signal",
                process.Id, timeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Graceful cancel for a cooperative child: write "CANCEL" to its stdin, then await exit up to
    /// <paramref name="gracePeriod"/>. If it does not exit in time, escalate to KillProcessTree.
    /// Returns true if the process exited (gracefully or after kill within the kill-wait).
    /// </summary>
    public async Task<bool> GracefulCancelAsync(Process process, TimeSpan gracePeriod, string reason)
    {
        if (process.HasExited)
        {
            return true;
        }

        try
        {
            if (process.StartInfo.RedirectStandardInput)
            {
                await process.StandardInput.WriteLineAsync("CANCEL");
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GracefulCancel: failed writing CANCEL to PID {ProcessId}", process.Id);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(gracePeriod);
            _logger.LogInformation("Process {ProcessId} exited gracefully after CANCEL ({Reason})", process.Id, reason);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Process {ProcessId} did not honor CANCEL within {Seconds}s — escalating to kill ({Reason})",
                process.Id,
                gracePeriod.TotalSeconds,
                reason);
            KillProcessTree(process, reason);
            await WaitForExitAfterKillAsync(process, TimeSpan.FromSeconds(5));
            return process.HasExited;
        }
    }

    /// <summary>
    /// Runs a short-lived process: track → wait for output → untrack → dispose.
    /// When <paramref name="killOnCancel"/> is true, cancellation kills the process tree before rethrowing.
    /// </summary>
    public async Task<ProcessCommandResult> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default,
        string? label = null,
        bool killOnCancel = true)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");

        Track(process);

        var cancelRegistration = killOnCancel && cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => KillProcessTree(process, label ?? startInfo.FileName))
            : default(CancellationTokenRegistration);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await WaitForExitAsync(process, cancellationToken);

            return new ProcessCommandResult
            {
                ExitCode = process.ExitCode,
                Output = await outputTask,
                Error = await errorTask
            };
        }
        finally
        {
            cancelRegistration.Dispose();
            Untrack(process);
            process.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var process in _activeProcesses.Values)
        {
            try
            {
                KillProcessTree(process, "ProcessManager dispose", log: false);
                process.Dispose();
            }
            catch
            {
                // Best effort cleanup
            }
        }

        _activeProcesses.Clear();
    }
}

/// <summary>
/// Result of a short-lived process run via <see cref="ProcessManager.RunAsync"/>.
/// </summary>
public class ProcessCommandResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
