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
            var processId = ReadProcessId(process);
            try
            {
                KillProcessTree(process, "application shutdown", log: false);
                await WaitAfterKillAsync(process, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error terminating process {ProcessId}", processId);
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
    /// <remarks>
    /// The id is read defensively because <see cref="Dispose"/> disposes every tracked process
    /// without untracking it, so a run still unwinding at shutdown reaches here holding a process
    /// whose handle is already gone.
    /// </remarks>
    public void Untrack(Process process)
    {
        var processId = ReadProcessId(process);
        if (processId.HasValue)
        {
            _activeProcesses.TryRemove(processId.Value, out _);
        }
    }

    /// <summary>
    /// Reads the process id, or null once the handle is gone.
    /// </summary>
    /// <remarks>
    /// A disposed <see cref="Process"/> throws <see cref="InvalidOperationException"/> from every
    /// member, <see cref="Process.Id"/> included, so a catch handler that reads the id to report the
    /// first failure throws a second exception that escapes the method meant to contain it. Callers
    /// read the id up front and log the local.
    /// </remarks>
    private static int? ReadProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True while the process is still running.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.HasExited"/> throws on a disposed process, so it cannot guard against the
    /// state it throws in. A handle that can no longer be read means the process is no longer ours
    /// to wait on or kill, which is what every caller here does with an exited one.
    /// </remarks>
    private static bool IsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Kills a process and its child processes. Safe to call when already exited or handle is stale.
    /// </summary>
    public bool KillProcessTree(Process process, string reason, bool log = true)
    {
        // The kill races the run's own thread disposing the process, so the id is read while the
        // handle is still readable and every catch below reports the local instead of the process.
        var processId = ReadProcessId(process);

        try
        {
            if (!IsRunning(process))
            {
                return false;
            }

            if (log)
            {
                _logger.LogWarning(
                    "Killing process tree {ProcessName} (PID: {ProcessId}): {Reason}",
                    process.ProcessName,
                    processId,
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
            _logger.LogDebug(ex, "Process handle invalid while killing PID {ProcessId}", processId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill process tree PID {ProcessId}", processId);
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
    public async Task WaitAfterKillAsync(Process process, TimeSpan timeout)
    {
        var processId = ReadProcessId(process);
        if (!IsRunning(process))
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(timeout);
            _logger.LogInformation("Process {ProcessId} terminated successfully", processId);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Process {ProcessId} did not exit within {Seconds}s after kill signal",
                processId, timeout.TotalSeconds);
        }
        catch (InvalidOperationException ex)
        {
            // The Process object lost its child between the running check above and the wait, so
            // there is nothing left to wait on and the caller's intent is already satisfied.
            _logger.LogDebug(ex, "Process {ProcessId} was already released before its exit could be awaited", processId);
        }
    }

    /// <summary>
    /// Graceful cancel for a cooperative child: write "CANCEL" to its stdin, then await exit up to
    /// <paramref name="gracePeriod"/>. If it does not exit in time, escalate to KillProcessTree.
    /// Returns true if the process exited (gracefully or after kill within the kill-wait).
    /// </summary>
    public async Task<bool> GracefulCancelAsync(Process process, TimeSpan gracePeriod, string reason)
    {
        var processId = ReadProcessId(process);
        if (!IsRunning(process))
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
            _logger.LogDebug(ex, "GracefulCancel: failed writing CANCEL to PID {ProcessId}", processId);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(gracePeriod);
            _logger.LogInformation("Process {ProcessId} exited gracefully after CANCEL ({Reason})", processId, reason);
            return true;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Process {ProcessId} did not honor CANCEL within {Seconds}s — escalating to kill ({Reason})",
                processId,
                gracePeriod.TotalSeconds,
                reason);
            KillProcessTree(process, reason);
            await WaitAfterKillAsync(process, TimeSpan.FromSeconds(5));
            return !IsRunning(process);
        }
        catch (InvalidOperationException ex)
        {
            // The Process object lost its child between the running check above and the wait. It
            // is gone, which is the outcome this method reports.
            _logger.LogDebug(ex, "Process {ProcessId} was already released during CANCEL ({Reason})", processId, reason);
            return true;
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
