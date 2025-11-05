using System.Collections.Concurrent;
using System.Diagnostics;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Centralized process management service that tracks all spawned processes
/// and ensures they are properly terminated during application shutdown.
/// This prevents orphaned Rust processes when the app stops.
/// </summary>
public class ProcessManager : IHostedService, IDisposable
{
    private readonly ILogger<ProcessManager> _logger;
    private readonly ConcurrentDictionary<int, Process> _activeProcesses = new();
    private bool _isShuttingDown = false;

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
        _isShuttingDown = true;
        _logger.LogWarning("ProcessManager stopping - terminating {Count} active processes", _activeProcesses.Count);

        var killTasks = _activeProcesses.Values.Select(process => Task.Run(async () =>
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.LogWarning("Terminating process {ProcessName} (PID: {ProcessId}) on shutdown",
                        process.ProcessName, process.Id);
                    process.Kill(entireProcessTree: true);

                    // Wait for process to exit with timeout
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                        _logger.LogInformation("Process {ProcessId} terminated successfully", process.Id);
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Process {ProcessId} did not exit within 5 seconds", process.Id);
                    }
                }
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
    /// Waits for a process to exit with cancellation token support.
    /// If cancelled, attempts to kill the process gracefully.
    /// Automatically tracks the process and cleans up on completion.
    /// </summary>
    public async Task WaitForProcessAsync(Process process, CancellationToken cancellationToken)
    {
        // Track the process
        _activeProcesses.TryAdd(process.Id, process);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation requested - try to kill the process
            if (!_isShuttingDown) // Only log if it's user cancellation, not app shutdown
            {
                _logger.LogWarning("Cancellation requested - terminating process {ProcessName} (PID: {ProcessId})",
                    process.ProcessName, process.Id);
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);

                    // Wait for the process to actually exit (with timeout)
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (TimeoutException)
                    {
                        _logger.LogWarning("Process {ProcessId} did not exit within 5 seconds after kill signal", process.Id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill process {ProcessId} during cancellation", process.Id);
            }

            throw; // Re-throw the cancellation exception
        }
        finally
        {
            // Remove from tracking when done
            _activeProcesses.TryRemove(process.Id, out _);
        }
    }

    public void Dispose()
    {
        foreach (var process in _activeProcesses.Values)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
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
