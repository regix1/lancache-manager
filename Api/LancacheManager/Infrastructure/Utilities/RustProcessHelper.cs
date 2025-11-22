using System.Diagnostics;
using System.Text.Json;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Helper class for common Rust process operations to eliminate code duplication
/// </summary>
public class RustProcessHelper
{
    private readonly ILogger<RustProcessHelper> _logger;
    private readonly ProcessManager _processManager;

    public RustProcessHelper(ILogger<RustProcessHelper> logger, ProcessManager processManager)
    {
        _logger = logger;
        _processManager = processManager;
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
    public async Task<ProcessExecutionResult> ExecuteProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo);

        if (process == null)
        {
            throw new Exception($"Failed to start process: {startInfo.FileName}");
        }

        // Start reading output asynchronously
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        // Wait for process to complete
        await _processManager.WaitForProcessAsync(process, cancellationToken);

        var output = await outputTask;
        var error = await errorTask;

        return new ProcessExecutionResult
        {
            ExitCode = process.ExitCode,
            Output = output,
            Error = error
        };
    }

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
    /// Reads an output JSON file, deserializes it, and cleans it up
    /// </summary>
    public async Task<T> ReadAndCleanupOutputJsonAsync<T>(string outputJsonPath, string operationName) where T : class
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
            while (!process.StandardOutput.EndOfStream)
            {
                var line = await process.StandardOutput.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    _logger.LogInformation("[{ProcessName}] {Line}", processName, line);
                }
            }
        });

        var stderrTask = Task.Run(async () =>
        {
            while (!process.StandardError.EndOfStream)
            {
                var line = await process.StandardError.ReadLineAsync();
                if (!string.IsNullOrEmpty(line))
                {
                    // Stderr may contain warnings or diagnostic info, log at debug level
                    _logger.LogDebug("[{ProcessName} stderr] {Line}", processName, line);
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
            // Resolve path to log_manager executable using IPathResolver pattern
            // For now, assume it's in the same directory as other Rust binaries
            var dataDirectory = Path.GetDirectoryName(logsPath) ?? ".";
            var rustBinaryPath = Path.Combine(dataDirectory, "..", "rust-processor", "target", "release", "log_manager.exe");

            if (!File.Exists(rustBinaryPath))
            {
                // Try Linux path
                rustBinaryPath = Path.Combine(dataDirectory, "..", "rust-processor", "target", "release", "log_manager");
            }

            ValidateRustBinaryExists(rustBinaryPath, "log_manager");

            // Build arguments based on command
            var arguments = command switch
            {
                "count" => $"count \"{logsPath}\" \"{progressFile ?? Path.GetTempFileName()}\"",
                "remove" when !string.IsNullOrEmpty(service) =>
                    $"remove \"{logsPath}\" \"{service}\" \"{progressFile ?? Path.GetTempFileName()}\"",
                _ => throw new ArgumentException($"Invalid command or missing parameters: {command}")
            };

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteProcessAsync(startInfo, CancellationToken.None);

            if (result.ExitCode == 0)
            {
                // Try to read output JSON if it exists
                var outputFile = progressFile ?? Path.GetTempFileName();
                object? data = null;

                if (File.Exists(outputFile))
                {
                    try
                    {
                        var jsonContent = await File.ReadAllTextAsync(outputFile);
                        data = System.Text.Json.JsonSerializer.Deserialize<object>(jsonContent);

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
        string? progressFile = null)
    {
        try
        {
            // Resolve path to corruption_manager executable
            var dataDirectory = Path.GetDirectoryName(logsPath) ?? ".";
            var rustBinaryPath = Path.Combine(dataDirectory, "..", "rust-processor", "target", "release", "corruption_manager.exe");

            if (!File.Exists(rustBinaryPath))
            {
                // Try Linux path
                rustBinaryPath = Path.Combine(dataDirectory, "..", "rust-processor", "target", "release", "corruption_manager");
            }

            ValidateRustBinaryExists(rustBinaryPath, "corruption_manager");

            // Build arguments based on command
            var arguments = command switch
            {
                "summary" => $"summary \"{logsPath}\" \"{cachePath}\" UTC",
                "remove" when !string.IsNullOrEmpty(service) =>
                    $"remove \"{logsPath}\" \"{cachePath}\" \"{service}\" \"{progressFile ?? Path.GetTempFileName()}\"",
                _ => throw new ArgumentException($"Invalid command or missing parameters: {command}")
            };

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteProcessAsync(startInfo, CancellationToken.None);

            if (result.ExitCode == 0)
            {
                // Try to read output JSON if it exists
                var outputFile = progressFile ?? Path.GetTempFileName();
                object? data = null;

                if (File.Exists(outputFile))
                {
                    try
                    {
                        var jsonContent = await File.ReadAllTextAsync(outputFile);
                        data = System.Text.Json.JsonSerializer.Deserialize<object>(jsonContent);

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
/// Result of a Rust executable execution with data payload
/// </summary>
public class RustExecutionResult
{
    public bool Success { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
}
