using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Helper class for common Rust process operations to eliminate code duplication
/// </summary>
public partial class RustProcessHelper
{
    private readonly ILogger<RustProcessHelper> _logger;
    private readonly ProcessManager _processManager;
    private readonly IPathResolver _pathResolver;
    private readonly IUnifiedOperationTracker _operationTracker;

    // Matches characters that could be used for argument injection or shell escaping
    [GeneratedRegex(@"[""'`$\\!;|&<>(){}\[\]\r\n\0]")]
    private static partial Regex DangerousArgumentCharsRegex();

    public RustProcessHelper(
        ILogger<RustProcessHelper> logger,
        ProcessManager processManager,
        IPathResolver pathResolver,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _processManager = processManager;
        _pathResolver = pathResolver;
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// Sanitizes a user-provided string for safe use as a process argument.
    /// Strips characters that could break quoted argument boundaries or enable injection.
    /// </summary>
    public static string SanitizeProcessArgument(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return DangerousArgumentCharsRegex().Replace(input, "");
    }

    /// <summary>
    /// Creates a standard ProcessStartInfo for Rust executables
    /// </summary>
    public ProcessStartInfo CreateProcessStartInfo(string executablePath, string arguments, string? workingDirectory = null)
    {
        return new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Enables ProcessManager.GracefulCancelAsync to write the cooperative "CANCEL" line to
            // the child's stdin so Rust binaries can stop cleanly before being force-killed.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath)
        };
    }

    /// <summary>
    /// Reads a JSON progress file with proper file sharing settings
    /// </summary>
    public async Task<T?> ReadProgressFileAsync<T>(string progressPath) where T : class
    {
        try
        {
            if (!File.Exists(progressPath))
            {
                return null;
            }

            string json;
            using (var fileStream = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fileStream))
            {
                json = await reader.ReadToEndAsync();
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Failed to read progress file (may not exist yet): {FilePath}", progressPath);
            return null;
        }
    }

    /// <summary>
    /// Safely deletes a temporary file with error handling
    /// </summary>
    public async Task DeleteTemporaryFileAsync(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Delete(filePath));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete temporary file: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// Executes a Rust process and returns output/error streams with proper handling
    /// </summary>
    public Task<ProcessExecutionResult> ExecuteProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken) =>
        ExecuteTrackedProcessAsync(startInfo, operationId: null, cancellationToken);

    /// <summary>
    /// Executes a Rust process with optional operation-tracker association so universal cancel /
    /// force-kill can terminate the process tree immediately (same pattern as cache clearing).
    /// </summary>
    public Task<ProcessExecutionResult> ExecuteTrackedProcessAsync(
        ProcessStartInfo startInfo,
        Guid? operationId,
        CancellationToken cancellationToken,
        string processLabel = "rust") =>
        ExecuteTrackedProcessWithProgressAsync<object>(
            startInfo,
            operationId,
            cancellationToken,
            progressFilePath: null,
            onProgress: null,
            processLabel);

    /// <summary>
    /// Core tracked-process runner: associate → execute → disassociate → dispose.
    /// Process kill on cancel is handled by <see cref="IUnifiedOperationTracker"/> when
    /// operationId is set; only untracked runs register a token callback to kill locally.
    /// </summary>
    public async Task<T> RunTrackedProcessAsync<T>(
        ProcessStartInfo startInfo,
        Guid? operationId,
        CancellationToken cancellationToken,
        Func<Process, Task<T>> executeAsync,
        string processLabel = "rust")
    {
        var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new Exception($"Failed to start process: {startInfo.FileName}");
        }

        _processManager.Track(process);

        // rust-5: associate immediately after Start (no intervening work) so a concurrent
        // tracker cancel/force-kill can find the process; then close the race by killing right
        // away if cancellation already fired before/while we wired everything up.
        if (operationId.HasValue)
        {
            _operationTracker.AssociateProcess(operationId.Value, process);
        }

        // P2-D: always register a token-cancel kill callback, even when an operationId is set.
        // A linked/host-shutdown token can cancel outside tracker.CancelOperation; without this the
        // finally would Untrack+Dispose the wrapper while leaving the OS child running. Idempotent
        // with the tracker's own kill via KillProcessTree's HasExited guard.
        var cancelRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() =>
                _processManager.KillProcessTree(process, $"{processLabel} token-cancel"))
            : default(CancellationTokenRegistration);

        // rust-5: if the token was already cancelled by the time we started, kill now so we don't
        // run the child to completion past a cancel that landed during startup.
        if (cancellationToken.IsCancellationRequested)
        {
            _processManager.KillProcessTree(process, $"{processLabel} already-cancelled at start");
        }

        try
        {
            return await executeAsync(process);
        }
        finally
        {
            cancelRegistration.Dispose();

            if (operationId.HasValue)
            {
                _operationTracker.DisassociateProcess(operationId.Value, process);
            }

            _processManager.Untrack(process);
            process.Dispose();
        }
    }

    /// <summary>
    /// Runs a tracked Rust process, optionally polling a JSON progress file until exit.
    /// Centralizes associate → cancel-kill → disassociate so callers don't duplicate that wiring.
    /// </summary>
    public Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressAsync<TProgress>(
        ProcessStartInfo startInfo,
        Guid? operationId,
        CancellationToken cancellationToken,
        string? progressFilePath,
        Func<TProgress, Task>? onProgress,
        string processLabel = "rust",
        int pollIntervalMs = 500) where TProgress : class =>
        RunTrackedProcessAsync(
            startInfo,
            operationId,
            cancellationToken,
            process => ExecuteWithProgressPollingAsync(
                process,
                cancellationToken,
                progressFilePath,
                onProgress,
                pollIntervalMs),
            processLabel: processLabel);

    private async Task<ProcessExecutionResult> ExecuteWithProgressPollingAsync<TProgress>(
        Process process,
        CancellationToken cancellationToken,
        string? progressFilePath,
        Func<TProgress, Task>? onProgress,
        int pollIntervalMs) where TProgress : class
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? pollTask = null;
        if (!string.IsNullOrEmpty(progressFilePath) && onProgress != null)
        {
            pollTask = Task.Run(async () =>
            {
                while (!process.HasExited && !pollCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(pollIntervalMs, pollCts.Token);

                    var progressData = await ReadProgressFileAsync<TProgress>(progressFilePath);
                    if (progressData != null)
                    {
                        await onProgress(progressData);
                    }
                }
            }, pollCts.Token);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await _processManager.WaitForExitAsync(process, cancellationToken);

            pollCts.Cancel();
            if (pollTask != null)
            {
                try { await pollTask; } catch (OperationCanceledException) { }
            }

            return new ProcessExecutionResult
            {
                ExitCode = process.ExitCode,
                Output = await outputTask,
                Error = await errorTask
            };
        }
        finally
        {
            // rust-7: when WaitForExitAsync throws on cancel, outputTask/errorTask would otherwise
            // go unobserved. Observe both and surface any captured stderr at Debug so a killed
            // binary still leaves diagnostics behind.
            await ObserveAndLogStderrAsync(outputTask, errorTask);
        }
    }

    /// <summary>
    /// Awaits the stdout/stderr read tasks defensively (swallowing read faults from a killed child)
    /// and logs any captured stderr at Debug. Used on cancel paths so output tasks are observed.
    /// </summary>
    private async Task ObserveAndLogStderrAsync(Task<string> outputTask, Task<string> errorTask)
    {
        try { await outputTask; } catch { /* read may fault when the child was killed */ }

        try
        {
            var stderr = await errorTask;
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("Cancelled/exited process stderr: {Stderr}", stderr);
            }
        }
        catch { /* read may fault when the child was killed */ }
    }

    /// <summary>
    /// Runs a tracked Rust process that emits newline-delimited JSON progress on stdout.
    /// </summary>
    public Task<ProcessExecutionResult> ExecuteTrackedProcessWithStdoutLinesAsync(
        ProcessStartInfo startInfo,
        Guid? operationId,
        CancellationToken cancellationToken,
        Func<string, Task> onStdoutLine,
        string processLabel = "rust") =>
        RunTrackedProcessAsync(
            startInfo,
            operationId,
            cancellationToken,
            async process =>
            {
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                try
                {
                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            await onStdoutLine(line);
                        }
                    }

                    await _processManager.WaitForExitAsync(process, cancellationToken);

                    return new ProcessExecutionResult
                    {
                        ExitCode = process.ExitCode,
                        Output = string.Empty,
                        Error = await stderrTask
                    };
                }
                catch (OperationCanceledException)
                {
                    // rust-7: observe stderrTask so a cancelled/killed binary's diagnostics are not lost.
                    try
                    {
                        var stderr = await stderrTask;
                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            _logger.LogDebug("Cancelled stdout-lines process stderr: {Stderr}", stderr);
                        }
                    }
                    catch { /* read may fault when the child was killed */ }

                    throw;
                }
            },
            processLabel: processLabel);

    /// <summary>
    /// Monitors a progress file and invokes callback with progress updates
    /// </summary>
    public async Task MonitorProgressFileAsync<T>(
        string progressPath,
        Func<T, Task> onProgressUpdate,
        CancellationToken cancellationToken,
        int pollIntervalMs = 500) where T : class
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(pollIntervalMs, cancellationToken);

                var progress = await ReadProgressFileAsync<T>(progressPath);
                if (progress != null)
                {
                    await onProgressUpdate(progress);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring progress file: {FilePath}", progressPath);
        }
    }

    /// <summary>
    /// Validates that a Rust binary exists at the specified path
    /// </summary>
    public void ValidateRustBinaryExists(string binaryPath, string binaryName)
    {
        if (!File.Exists(binaryPath))
        {
            var errorMsg = $"{binaryName} binary not found at {binaryPath}. Please ensure the Rust binaries are built.";
            _logger.LogError(errorMsg);
            throw new FileNotFoundException(errorMsg);
        }
    }

    /// <summary>
    /// Reads an output JSON file and deserializes it (keeps the file for history)
    /// </summary>
    public async Task<T> ReadOutputJsonAsync<T>(string outputJsonPath, string operationName) where T : class
    {
        if (!File.Exists(outputJsonPath))
        {
            _logger.LogError("[{Operation}] Output JSON file not found: {Path}", operationName, outputJsonPath);
            throw new FileNotFoundException($"{operationName} output file not found: {outputJsonPath}");
        }

        var jsonContent = await File.ReadAllTextAsync(outputJsonPath);
        _logger.LogInformation("[{Operation}] Read JSON output, length: {Length}", operationName, jsonContent.Length);

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<T>(jsonContent, options);

        if (result == null)
        {
            _logger.LogError("[{Operation}] Failed to parse output JSON", operationName);
            throw new Exception($"Failed to parse {operationName} output JSON");
        }

        return result;
    }

    /// <summary>
    /// Reads an output JSON file, deserializes it, and cleans it up
    /// </summary>
    public async Task<T> ReadAndCleanupOutputJsonAsync<T>(string outputJsonPath, string operationName) where T : class
    {
        var result = await ReadOutputJsonAsync<T>(outputJsonPath, operationName);

        // Clean up temporary JSON file
        await DeleteTemporaryFileAsync(outputJsonPath);

        return result;
    }

    /// <summary>
    /// Creates monitoring tasks for stdout and stderr of a process
    /// </summary>
    public (Task stdoutTask, Task stderrTask) CreateOutputMonitoringTasks(Process process, string processName)
    {
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogInformation("[{ProcessName}] {Line}", processName, line);
                }
            }
        });

        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    // Stderr may contain warnings or diagnostic info, log at debug level
                    _logger.LogInformation("[{ProcessName} stderr] {Line}", processName, line);
                }
            }
        });

        return (stdoutTask, stderrTask);
    }

    /// <summary>
    /// Waits for output monitoring tasks to complete with timeout
    /// </summary>
    public async Task WaitForOutputTasksAsync(Task stdoutTask, Task stderrTask, TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timeout waiting for stdout/stderr tasks to complete after {Timeout}s", timeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for stdout/stderr tasks");
        }
    }

    /// <summary>
    /// Runs the log_manager Rust executable for counting or removing log entries
    /// </summary>
    public async Task<RustExecutionResult> RunLogManagerAsync(
        string command,
        string logsPath,
        string? progressFile = null,
        string? service = null)
    {
        try
        {
            // Sanitize user-provided service name to prevent process argument injection
            if (service != null)
                service = SanitizeProcessArgument(service);

            // Use path resolver to get the correct Rust binary path for the current platform
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();
            ValidateRustBinaryExists(rustBinaryPath, "log_manager");

            // Create temp file path once if not provided
            var outputFile = progressFile ?? Path.GetTempFileName();

            // Build arguments based on command
            var arguments = command switch
            {
                "count" => $"count \"{logsPath}\" \"{outputFile}\"",
                "remove" when !string.IsNullOrEmpty(service) =>
                    $"remove \"{logsPath}\" \"{service}\" \"{outputFile}\"",
                _ => throw new ArgumentException($"Invalid command or missing parameters: {command}")
            };

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteProcessAsync(startInfo, CancellationToken.None);

            if (result.ExitCode == 0)
            {
                // Try to read output JSON if it exists
                object? data = null;

                if (File.Exists(outputFile))
                {
                    try
                    {
                        var jsonContent = await File.ReadAllTextAsync(outputFile);

                        // Only attempt to deserialize if content is not empty
                        if (!string.IsNullOrWhiteSpace(jsonContent))
                        {
                            data = System.Text.Json.JsonSerializer.Deserialize<object>(jsonContent);
                        }
                        else
                        {
                            _logger.LogDebug("log_manager output file is empty");
                        }

                        // Clean up temp file
                        if (progressFile == null)
                        {
                            await DeleteTemporaryFileAsync(outputFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse log_manager output JSON");
                    }
                }

                return new RustExecutionResult
                {
                    Success = true,
                    Data = data,
                    Error = null
                };
            }

            return new RustExecutionResult
            {
                Success = false,
                Data = null,
                Error = result.Error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running log_manager");
            return new RustExecutionResult
            {
                Success = false,
                Data = null,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Runs the corruption_manager Rust executable for detecting or removing corrupted files
    /// </summary>
    public async Task<RustExecutionResult> RunCorruptionManagerAsync(
        string command,
        string logsPath,
        string cachePath,
        string? service = null,
        string? progressFile = null,
        CancellationToken cancellationToken = default,
        int threshold = 3,
        bool compareToCacheLogs = true,
        bool detectRedownloads = false,
        Guid? operationId = null)
    {
        try
        {
            // Sanitize user-provided service name to prevent process argument injection
            if (service != null)
                service = SanitizeProcessArgument(service);

            // Use path resolver to get the correct Rust binary path for the current platform
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();
            ValidateRustBinaryExists(rustBinaryPath, "corruption_manager");

            // Create temp file path once if not provided
            var outputFile = progressFile ?? Path.GetTempFileName();

            // Build arguments based on command
            var noCacheCheckFlag = !compareToCacheLogs ? " --no-cache-check" : "";
            var redownloadFlag = detectRedownloads ? " --detect-redownloads" : "";
            var arguments = command switch
            {
                "summary" => $"summary \"{logsPath}\" \"{cachePath}\" UTC {threshold}{noCacheCheckFlag}{redownloadFlag}",
                "remove" when !string.IsNullOrEmpty(service) =>
                    $"remove \"{logsPath}\" \"{cachePath}\" \"{service}\" \"{outputFile}\" {threshold}{noCacheCheckFlag}{redownloadFlag}",
                _ => throw new ArgumentException($"Invalid command or missing parameters: {command}")
            };

            _logger.LogInformation("[corruption_manager] Executing: {Binary} {Args}", rustBinaryPath, arguments);

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteTrackedProcessAsync(
                startInfo,
                operationId,
                cancellationToken,
                "corruption_manager");

            // Log stdout and stderr for debugging
            if (!string.IsNullOrEmpty(result.Output))
            {
                _logger.LogInformation("[corruption_manager] stdout: {Output}", result.Output);
            }
            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogWarning("[corruption_manager] stderr: {Error}", result.Error);
            }

            if (result.ExitCode == 0)
            {
                // Try to read output JSON if it exists
                object? data = null;

                if (File.Exists(outputFile))
                {
                    try
                    {
                        var jsonContent = await File.ReadAllTextAsync(outputFile, cancellationToken);

                        // Only attempt to deserialize if content is not empty
                        if (!string.IsNullOrWhiteSpace(jsonContent))
                        {
                            data = System.Text.Json.JsonSerializer.Deserialize<object>(jsonContent);
                        }
                        else
                        {
                            _logger.LogDebug("corruption_manager output file is empty");
                        }

                        // Clean up temp file
                        if (progressFile == null)
                        {
                            await DeleteTemporaryFileAsync(outputFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse corruption_manager output JSON");
                    }
                }

                return new RustExecutionResult
                {
                    Success = true,
                    Data = data,
                    Error = null
                };
            }

            return new RustExecutionResult
            {
                Success = false,
                Data = null,
                Error = result.Error
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running corruption_manager");
            return new RustExecutionResult
            {
                Success = false,
                Data = null,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Runs the cache_eviction_scan Rust executable
    /// </summary>
    public async Task<RustExecutionResult> RunEvictionScanAsync(
        string datasourceConfigPath,
        string? progressFile = null,
        CancellationToken cancellationToken = default,
        Guid? operationId = null)
    {
        try
        {
            var rustBinaryPath = _pathResolver.GetRustEvictionScanPath();
            ValidateRustBinaryExists(rustBinaryPath, "cache_eviction_scan");

            var progressArg = progressFile ?? "none";
            var arguments = $"\"{datasourceConfigPath}\" \"{progressArg}\"";

            _logger.LogInformation("[cache_eviction_scan] Executing: {Binary} {Args}", rustBinaryPath, arguments);

            // services-3: fail fast if cancellation already fired so we never spawn the child.
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteTrackedProcessAsync(
                startInfo,
                operationId,
                cancellationToken,
                "cache_eviction_scan");

            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogInformation("[cache_eviction_scan] stderr: {Error}", result.Error);
            }

            if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Output))
            {
                try
                {
                    var data = System.Text.Json.JsonSerializer.Deserialize<object>(result.Output);
                    return new RustExecutionResult { Success = true, Data = data, Error = null };
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse cache_eviction_scan stdout JSON");
                }
            }

            return new RustExecutionResult
            {
                Success = result.ExitCode == 0,
                Data = null,
                Error = result.Error
            };
        }
        catch (OperationCanceledException)
        {
            // services-3: let cancellation propagate so the caller's OCE handling runs instead of
            // being masked as a generic failure result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running cache_eviction_scan");
            return new RustExecutionResult { Success = false, Data = null, Error = ex.Message };
        }
    }
}

/// <summary>
/// Result of a process execution
/// </summary>
public class ProcessExecutionResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Result of a Rust executable execution with data result
/// </summary>
public class RustExecutionResult
{
    public bool Success { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
}
