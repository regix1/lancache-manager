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

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Default interval (ms) between progress-file polls. Shared so the several callers that poll a
    /// Rust progress JSON file (db reset, log removal, cache clear, corruption removal, eviction scan)
    /// stay consistent instead of scattering 250/500 literals.
    /// </summary>
    public const int DefaultProgressPollMs = 500;

    /// <summary>
    /// Minimum interval (ms) between SignalR progress broadcasts for stdout-tick-driven Rust
    /// operations (game detection, cache size scan, eviction scan, cache clearing). Rust can tick
    /// many times per second and every broadcast is a re-render on every connected client (mobile
    /// browsers crash under the flood), so emitters gate on this unless the stage key changed.
    /// Terminal state always travels on the operation's *Complete event, never a gated progress tick.
    /// </summary>
    public const int ProgressEmitMinIntervalMs = 250;

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
    /// Creates a standard Rust process without shell-style argument parsing. Each value is added
    /// as one operating-system argument, preserving spaces and special characters verbatim.
    /// </summary>
    public ProcessStartInfo CreateProcessStartInfo(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null)
    {
        var startInfo = CreateProcessStartInfo(executablePath, string.Empty, workingDirectory);
        foreach (var argument in arguments)
        {
            ArgumentNullException.ThrowIfNull(argument);
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
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

            // An existing-but-empty file means the Rust side hasn't written its first checkpoint
            // yet (C# pre-creates progress temps via GetTempFileName, and a stdout "started" event
            // can wake an event-driven reader before that first write lands). Same semantics as a
            // missing file - not a parse failure.
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<T>(json, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read progress file: {FilePath}", progressPath);
            return null;
        }
    }

    /// <summary>
    /// Safely deletes a temporary file with error handling
    /// </summary>
    public async Task DeleteTempFileAsync(string filePath)
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
        int pollIntervalMs = DefaultProgressPollMs,
        Action<string>? onStderrLine = null,
        int? maxRetainedStderrChars = null) where TProgress : class =>
        RunTrackedProcessAsync(
            startInfo,
            operationId,
            cancellationToken,
            process => ExecuteWithProgressPollingAsync(
                process,
                cancellationToken,
                progressFilePath,
                onProgress,
                pollIntervalMs,
                onStderrLine,
                maxRetainedStderrChars),
            processLabel: processLabel);

    private async Task<ProcessExecutionResult> ExecuteWithProgressPollingAsync<TProgress>(
        Process process,
        CancellationToken cancellationToken,
        string? progressFilePath,
        Func<TProgress, Task>? onProgress,
        int pollIntervalMs,
        Action<string>? onStderrLine,
        int? maxRetainedStderrChars) where TProgress : class
    {
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? pollTask = null;
        if (!string.IsNullOrEmpty(progressFilePath) && onProgress != null)
        {
            pollTask = Task.Run(
                () => PollProgressFileLoopAsync<TProgress>(
                    progressFilePath,
                    onProgress,
                    () => !process.HasExited,
                    pollIntervalMs,
                    pollCts.Token),
                pollCts.Token);
        }

        // Stream reads deliberately do not use the operation token. Cancellation kills the child;
        // EOF then drains diagnostics without abandoning a pipe or deadlocking process shutdown.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = PumpStderrLinesAsync(
            process.StandardError,
            onStderrLine,
            maxRetainedStderrChars,
            ex => _logger.LogWarning(ex, "Rust stderr observer threw; continuing to drain the child pipe"));

        try
        {
            await _processManager.WaitForExitAsync(process, cancellationToken);

            // The child atomically writes its terminal checkpoint immediately before exit. A
            // periodic poll can lose that last update when process exit wins the race, so read it
            // once synchronously before stopping the monitor.
            if (!string.IsNullOrEmpty(progressFilePath) && onProgress != null)
            {
                var terminalProgress = await ReadProgressFileAsync<TProgress>(progressFilePath);
                if (terminalProgress != null)
                {
                    await onProgress(terminalProgress);
                }
            }

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
            pollCts.Cancel();
            if (pollTask != null)
            {
                try { await pollTask; } catch (OperationCanceledException) { }
            }

            // A killed child normally closes both pipes immediately. Bound only the exceptional
            // platform case so cancellation cannot strand the worker forever.
            await Task.WhenAll(
                ObserveReaderTaskAsync(outputTask, "stdout"),
                ObserveReaderTaskAsync(errorTask, "stderr"));
        }
    }

    private async Task ObserveReaderTaskAsync(Task<string> task, string streamName)
    {
        try
        {
            var content = await task.WaitAsync(TimeSpan.FromSeconds(2));
            if (streamName == "stderr" && !string.IsNullOrWhiteSpace(content))
            {
                _logger.LogDebug("Cancelled/exited process stderr: {Stderr}", content);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("Timed out draining cancelled/exited process {Stream}", streamName);
            _ = task.ContinueWith(
                completed => _logger.LogDebug(
                    completed.Exception,
                    "Late cancelled/exited process {Stream} drain faulted",
                    streamName),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelled/exited process {Stream} drain faulted", streamName);
        }
    }

    internal static async Task<string> PumpStderrLinesAsync(
        TextReader reader,
        Action<string>? onStderrLine,
        int? maxRetainedChars,
        Action<Exception>? onCallbackError = null)
    {
        if (maxRetainedChars is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetainedChars));
        }

        var tail = new BoundedTextTail(maxRetainedChars);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            tail.AppendLine(line);
            if (onStderrLine == null)
            {
                continue;
            }

            try
            {
                onStderrLine(line);
            }
            catch (Exception ex)
            {
                onCallbackError?.Invoke(ex);
            }
        }

        return tail.ToString();
    }

    private async Task PollProgressFileLoopAsync<T>(
        string progressFilePath,
        Func<T, Task> onProgress,
        Func<bool> shouldContinue,
        int pollIntervalMs,
        CancellationToken ct) where T : class
    {
        try
        {
            while (!ct.IsCancellationRequested && shouldContinue())
            {
                await Task.Delay(pollIntervalMs, ct);

                var progressData = await ReadProgressFileAsync<T>(progressFilePath);
                if (progressData != null)
                {
                    await onProgress(progressData);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error monitoring progress file: {FilePath}", progressFilePath);
        }
    }

    /// <summary>
    /// Runs a tracked Rust process, consuming LIVE structured progress events from its stdout
    /// (progress_events.rs's started/progress/complete NDJSON protocol) instead of polling a
    /// progress file. Only a binary that actually emits this protocol (every migrated binary now
    /// does: cache_clear, cache_game_detect, log_processor, cache_size, cache_eviction_scan,
    /// corruption_manager, db_reset, log_manager, and cache-removal binaries) should use
    /// this; a binary that only writes a progress file without emitting events must keep using
    /// <see cref="ExecuteTrackedProcessWithProgressAsync{TProgress}"/>.
    /// The Rust side keeps writing its progress file unchanged (crash-recovery checkpoint); this
    /// only changes what C# reads live, replacing the up-to-<see cref="DefaultProgressPollMs"/>ms
    /// poll delay with an event-driven reaction to each line Rust actually emits.
    /// </summary>
    public Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
        ProcessStartInfo startInfo,
        Guid? operationId,
        CancellationToken cancellationToken,
        Func<RustProgressEvent, Task>? onProgressEvent,
        string processLabel = "rust") =>
        RunTrackedProcessAsync(
            startInfo,
            operationId,
            cancellationToken,
            process => ExecuteWithProgressEventsAsync(
                process,
                cancellationToken,
                onProgressEvent,
                processLabel),
            processLabel: processLabel);

    private async Task<ProcessExecutionResult> ExecuteWithProgressEventsAsync(
        Process process,
        CancellationToken cancellationToken,
        Func<RustProgressEvent, Task>? onProgressEvent,
        string processLabel)
    {
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            var output = await ConsumeStdoutProgressEventsAsync(process, onProgressEvent, processLabel, cancellationToken);

            await _processManager.WaitForExitAsync(process, cancellationToken);

            return new ProcessExecutionResult
            {
                ExitCode = process.ExitCode,
                Output = output,
                Error = await errorTask
            };
        }
        finally
        {
            // Mirrors ExecuteWithProgressPollingAsync/LogStderrAsync's defensive stderr
            // observation: on a cancel/kill the read above may fault or never have been awaited
            // in the try block; observe it here so it never goes unobserved, and log any captured
            // stderr at Debug the same way the polling path does.
            try
            {
                var stderr = await errorTask;
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    _logger.LogDebug("[{ProcessLabel}] Cancelled/exited process stderr: {Stderr}", processLabel, stderr);
                }
            }
            catch { /* read may fault when the child was killed */ }
        }
    }

    /// <summary>
    /// Reads a process's stdout continuously via ReadLineAsync (no polling, mirrors
    /// RustSpeedTrackerService's stdout-consumption technique), parsing each non-empty line as a
    /// progress_events.rs-shaped JSON event and invoking <paramref name="onProgressEvent"/> for
    /// each one successfully parsed. A malformed/partial line is logged at Debug and skipped
    /// rather than throwing or being silently swallowed. Returns the raw stdout text
    /// (newline-joined) for callers that also want the verbatim output, mirroring what
    /// ReadToEndAsync captured on the old polling path. Public so a caller with a bespoke process
    /// closure (e.g. one that also needs the raw <see cref="Process"/> for other reasons) can
    /// consume the live event stream directly instead of going through
    /// <see cref="ExecuteTrackedProcessWithProgressEventsAsync"/>.
    /// </summary>
    public async Task<string> ConsumeStdoutProgressEventsAsync(
        Process process,
        Func<RustProgressEvent, Task>? onProgressEvent,
        string processLabel,
        CancellationToken cancellationToken)
    {
        var stdoutLines = new List<string>();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            stdoutLines.Add(line);

            if (onProgressEvent == null)
            {
                continue;
            }

            // Progress envelopes are always emitted as a complete single-line JSON object. Some
            // binaries (e.g. cache_size) also print their final result PRETTY-PRINTED on this same
            // stream - those fragment lines ("{", "  \"totalBytes\": ...,", "}") are raw output,
            // not malformed events, so skip them without attempting a parse.
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
            {
                continue;
            }

            RustProgressEvent? progressEvent;
            try
            {
                progressEvent = JsonSerializer.Deserialize<RustProgressEvent>(trimmed, _jsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "[{ProcessLabel}] Skipping unparseable stdout line: {Line}", processLabel, line);
                continue;
            }

            // A JSON line with no "event" field is not a progress_events.rs envelope (every emit
            // method sets a non-empty event); e.g. cache_eviction_scan prints its one-shot
            // ScanResult on this same stdout stream. Skip those so a non-envelope line can't fire a
            // spurious extra terminal callback (duplicate SignalR emit / progress-file read).
            if (progressEvent != null && !string.IsNullOrEmpty(progressEvent.Event))
            {
                try
                {
                    await onProgressEvent(progressEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{ProcessLabel}] Progress event callback threw for event {Event} ({StageKey})", processLabel, progressEvent.Event, progressEvent.StageKey);
                }
            }
        }

        return string.Join(Environment.NewLine, stdoutLines);
    }

    /// <summary>
    /// Monitors a progress file and invokes callback with progress updates
    /// </summary>
    public Task MonitorProgressFileAsync<T>(
        string progressPath,
        Func<T, Task> onProgressUpdate,
        CancellationToken cancellationToken,
        int pollIntervalMs = DefaultProgressPollMs) where T : class =>
        PollProgressFileLoopAsync<T>(
            progressPath,
            onProgressUpdate,
            () => true,
            pollIntervalMs,
            cancellationToken);

    /// <summary>
    /// Validates that a Rust binary exists at the specified path
    /// </summary>
    public void EnsureBinaryExists(string binaryPath, string binaryName)
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
    public async Task<T> ReadOutputJsonAsync<T>(
        string outputJsonPath,
        string operationName,
        CancellationToken cancellationToken = default) where T : class
    {
        if (!File.Exists(outputJsonPath))
        {
            _logger.LogError("[{Operation}] Output JSON file not found: {Path}", operationName, outputJsonPath);
            throw new FileNotFoundException($"{operationName} output file not found: {outputJsonPath}");
        }

        // Deserialize straight off the stream - report payloads (corruption chunk lists,
        // removal reports) can run to hundreds of MB, and ReadAllText would hold the whole
        // document as a string next to the parsed result.
        T? result;
        await using (var fileStream = File.OpenRead(outputJsonPath))
        {
            _logger.LogInformation("[{Operation}] Reading JSON output, {Length} bytes", operationName, fileStream.Length);
            result = await JsonSerializer.DeserializeAsync<T>(
                fileStream,
                _jsonOptions,
                cancellationToken);
        }

        if (result == null)
        {
            _logger.LogError("[{Operation}] Failed to parse output JSON", operationName);
            throw new InvalidDataException($"Failed to parse {operationName} output JSON");
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
        await DeleteTempFileAsync(outputJsonPath);

        return result;
    }

    /// <summary>
    /// Streams a rust binary's output file into a JsonElement without materializing the
    /// document as a string first. Returns null (with a debug log) for an empty file;
    /// parse failures propagate so callers keep their existing cleanup semantics.
    /// </summary>
    private async Task<object?> DeserializeOutputFileAsync(
        string outputFile,
        string binaryName,
        CancellationToken cancellationToken = default)
    {
        await using var fileStream = File.OpenRead(outputFile);
        if (fileStream.Length == 0)
        {
            _logger.LogDebug("{Binary} output file is empty", binaryName);
            return null;
        }

        return await JsonSerializer.DeserializeAsync<object>(fileStream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Waits for output monitoring tasks to complete with timeout
    /// </summary>
    public async Task AwaitOutputTasksAsync(Task stdoutTask, Task stderrTask, TimeSpan timeout)
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
            EnsureBinaryExists(rustBinaryPath, "log_manager");

            // Create temp file path once if not provided
            var outputFile = progressFile ?? Path.GetTempFileName();

            // Build arguments based on command
            var arguments = command switch
            {
                "count" => $"count \"{logsPath}\" \"{outputFile}\" --progress",
                "remove" when !string.IsNullOrEmpty(service) =>
                    $"remove \"{logsPath}\" \"{service}\" \"{outputFile}\" --progress",
                _ => throw new ArgumentException($"Invalid command or missing parameters: {command}")
            };

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            // No operationId/UI tracking for this call: it's a synchronous one-shot RPC embedded in
            // a GET handler (LogsController.GetServiceCountsForDatasourceAsync, invoked per-datasource),
            // not a registered IUnifiedOperationTracker operation - there is no cancellation token or
            // SignalR notification surface to wire an operationId into. Switching to the stdout-events
            // transport still removes the buffered ReadToEndAsync in favor of the shared event-driven
            // reader (log_service_manager.rs now emits ProgressReporter events); the callback only logs,
            // mirroring RustLogProcessorService's minimal "logging only" hybrid.
            var result = await ExecuteTrackedProcessWithProgressEventsAsync(
                startInfo,
                operationId: null,
                CancellationToken.None,
                onProgressEvent: evt =>
                {
                    _logger.LogDebug("[log_manager] {Event} ({StageKey})", evt.Event, evt.StageKey);
                    return Task.CompletedTask;
                },
                "log_manager");

            if (result.ExitCode == 0)
            {
                // Try to read output JSON if it exists
                object? data = null;

                if (File.Exists(outputFile))
                {
                    try
                    {
                        data = await DeserializeOutputFileAsync(outputFile, "log_manager");

                        // Clean up temp file
                        if (progressFile == null)
                        {
                            await DeleteTempFileAsync(outputFile);
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
                    ExitCode = 0,
                    Data = data,
                    Error = null
                };
            }

            return new RustExecutionResult
            {
                Success = false,
                ExitCode = result.ExitCode,
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
                ExitCode = -1,
                Data = null,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Streams the configured datasource's canonical plain/gzip log rotations through the Rust
    /// log manager and returns its final typed line count. Rust cancellation remains an
    /// <see cref="OperationCanceledException"/> and never becomes a zero count.
    /// </summary>
    public virtual async Task<LogLineCountResult> CountLogLinesAsync(
        string logsPath,
        CancellationToken cancellationToken = default)
    {
        var progress = await RunLogFileOperationAsync(
            "count-lines",
            logsPath,
            cancellationToken);

        return new LogLineCountResult(
            progress.LinesProcessed,
            progress.FilesProcessed);
    }

    /// <summary>
    /// Deletes one host-validated active log path through the Rust log manager and returns the
    /// pre-delete byte count reported by Rust.
    /// </summary>
    public virtual async Task<LogFileDeletionResult> DeleteLogFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var progress = await RunLogFileOperationAsync(
            "delete-file",
            filePath,
            cancellationToken);

        return new LogFileDeletionResult(progress.BytesDeleted);
    }

    private async Task<LogManagerFileProgress> RunLogFileOperationAsync(
        string command,
        string path,
        CancellationToken cancellationToken)
    {
        var progressFile = Path.GetTempFileName();
        try
        {
            var rustBinaryPath = _pathResolver.GetRustLogManagerPath();
            EnsureBinaryExists(rustBinaryPath, "log_service_manager");

            // ArgumentList preserves configured paths verbatim without shell parsing or quote
            // construction. CreateProcessStartInfo still supplies the shared redirected/tracked
            // process configuration used by every Rust wrapper.
            var startInfo = CreateProcessStartInfo(rustBinaryPath, string.Empty);
            startInfo.ArgumentList.Add(command);
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add(progressFile);
            startInfo.ArgumentList.Add("--progress");

            var result = await ExecuteTrackedProcessWithProgressEventsAsync(
                startInfo,
                operationId: null,
                cancellationToken,
                onProgressEvent: evt =>
                {
                    _logger.LogDebug(
                        "[log_service_manager:{Command}] {Event} ({StageKey})",
                        command,
                        evt.Event,
                        evt.StageKey);
                    return Task.CompletedTask;
                },
                processLabel: $"log_service_manager:{command}");

            result.EnsureSuccess("log_service_manager", command);
            cancellationToken.ThrowIfCancellationRequested();

            var progress = await ReadOutputJsonAsync<LogManagerFileProgress>(
                progressFile,
                $"log_service_manager {command}",
                cancellationToken);

            if (string.Equals(progress.Status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationCanceledException(
                    $"log_service_manager {command} was cancelled.",
                    cancellationToken);
            }

            if (!string.Equals(progress.Status, "completed", StringComparison.OrdinalIgnoreCase) ||
                progress.IsProcessing)
            {
                throw new InvalidDataException(
                    $"log_service_manager {command} did not produce a completed result.");
            }

            return progress;
        }
        finally
        {
            await DeleteTempFileAsync(progressFile);
        }
    }

    private sealed class LogManagerFileProgress
    {
        [System.Text.Json.Serialization.JsonPropertyName("is_processing")]
        public required bool IsProcessing { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public required string Status { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("lines_processed")]
        public required long LinesProcessed { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("files_processed")]
        public required long FilesProcessed { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("bytes_deleted")]
        public required long BytesDeleted { get; init; }
    }

    /// <summary>
    /// Runs the corruption_manager Rust executable for detecting or removing corrupted files
    /// </summary>
    public Task<RustExecutionResult> RunCorruptionManagerAsync(
        string command,
        string logsPath,
        string cachePath,
        string? service = null,
        string? evidenceFile = null,
        string? progressFile = null,
        CancellationToken cancellationToken = default,
        Guid? operationId = null,
        Func<RustProgressEvent, Task>? onProgressEvent = null) =>
        RunCorruptionManagerCommandAsync(
            command,
            logsPath,
            cachePath,
            service,
            evidenceFile,
            progressFile,
            cancellationToken,
            operationId,
            onProgressEvent);

    private async Task<RustExecutionResult> RunCorruptionManagerCommandAsync(
        string command,
        string logsPath,
        string cachePath,
        string? service,
        string? evidenceFile,
        string? progressFile,
        CancellationToken cancellationToken,
        Guid? operationId,
        Func<RustProgressEvent, Task>? onProgressEvent)
    {
        // D-rust-4: the Rust `remove` command's only data sink is its progress_json file, which the
        // CALLER monitors on a 500ms poll loop (CacheController). Previously that same caller path was
        // ALSO passed here as the "output JSON" we read back after exit — so the poller and the
        // result-reader raced on one file (a poll could read a half-written final-summary tick).
        // Keep them on DISTINCT paths: the Rust binary writes progress to `progressFile` (the poll
        // path) while the post-exit result read targets a SEPARATE internal temp. (Callers consume
        // only Success/Error, not Data, so an empty output read is harmless.) The temp is created up
        // front and removed unconditionally in the finally so it never leaks.
        var progressArg = progressFile;
        var outputFile = Path.GetTempFileName();

        try
        {
            // Sanitize user-provided service name to prevent process argument injection
            if (service != null)
                service = SanitizeProcessArgument(service);

            // Use path resolver to get the correct Rust binary path for the current platform
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();
            EnsureBinaryExists(rustBinaryPath, "corruption_manager");

            // Removal consumes the server-persisted evidence file. Mode, threshold,
            // URLs, and paths are never reconstructed from caller-selected flags.
            var arguments = BuildCorruptionManagerArguments(
                command,
                logsPath,
                cachePath,
                service,
                evidenceFile,
                progressArg);

            _logger.LogInformation("[corruption_manager] Executing: {Binary} {Args}", rustBinaryPath, arguments);

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteTrackedProcessWithProgressEventsAsync(
                startInfo,
                operationId,
                cancellationToken,
                onProgressEvent,
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
                // Try to read the internal output JSON if the binary wrote one. This temp is DISTINCT
                // from the caller-polled progress file (see progressArg above), so reading it can never
                // collide with the 500ms poll. It is cleaned up unconditionally in the finally.
                object? data = null;

                if (File.Exists(outputFile))
                {
                    try
                    {
                        data = await DeserializeOutputFileAsync(outputFile, "corruption_manager", cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to parse corruption_manager output JSON");
                    }
                }

                return new RustExecutionResult
                {
                    Success = true,
                    ExitCode = result.ExitCode,
                    Data = data,
                    Error = null
                };
            }

            return new RustExecutionResult
            {
                Success = false,
                ExitCode = result.ExitCode,
                Data = null,
                Error = result.Error
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running corruption_manager");
            return new RustExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Data = null,
                Error = ex.Message
            };
        }
        finally
        {
            // Always remove the internal output-JSON temp (distinct from the caller-polled progress
            // file). The caller owns cleanup of its own progress file.
            await DeleteTempFileAsync(outputFile);
        }
    }

    internal static string BuildCorruptionManagerArguments(
        string command,
        string logsPath,
        string cachePath,
        string? service,
        string? evidenceFile,
        string? progressFile) => command switch
        {
            "remove" when !string.IsNullOrEmpty(service)
                && !string.IsNullOrEmpty(cachePath)
                && !string.IsNullOrEmpty(evidenceFile)
                && !string.IsNullOrEmpty(progressFile) =>
                $"remove \"{logsPath}\" \"{cachePath}\" \"{service}\" \"{progressFile}\" --evidence-file \"{evidenceFile}\" --progress",
            "remove-structural" when !string.IsNullOrEmpty(cachePath)
                && !string.IsNullOrEmpty(evidenceFile)
                && !string.IsNullOrEmpty(progressFile) =>
                $"remove-structural \"{cachePath}\" \"{progressFile}\" --evidence-file \"{evidenceFile}\" --progress",
            _ => throw new ArgumentException($"Invalid command or missing parameters: {command}")
        };

    /// <summary>
    /// Runs the cache_eviction_scan Rust executable
    /// </summary>
    public async Task<RustExecutionResult> RunEvictionScanAsync(
        string datasourceConfigPath,
        string? progressFile = null,
        CancellationToken cancellationToken = default,
        Guid? operationId = null,
        Func<RustProgressEvent, Task>? onProgressEvent = null)
    {
        try
        {
            var rustBinaryPath = _pathResolver.GetRustEvictionScanPath();
            EnsureBinaryExists(rustBinaryPath, "cache_eviction_scan");

            var progressArg = progressFile ?? "none";
            var arguments = $"\"{datasourceConfigPath}\" \"{progressArg}\" --progress";

            _logger.LogInformation("[cache_eviction_scan] Executing: {Binary} {Args}", rustBinaryPath, arguments);

            // services-3: fail fast if cancellation already fired so we never spawn the child.
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = CreateProcessStartInfo(rustBinaryPath, arguments);
            var result = await ExecuteTrackedProcessWithProgressEventsAsync(
                startInfo,
                operationId,
                cancellationToken,
                onProgressEvent,
                "cache_eviction_scan");

            if (!string.IsNullOrEmpty(result.Error))
            {
                _logger.LogInformation("[cache_eviction_scan] stderr: {Error}", result.Error);
            }

            if (result.ExitCode == 0)
            {
                // cache_eviction_scan.rs prints its one-shot ScanResult JSON as the FINAL stdout
                // line, AFTER every NDJSON progress-event envelope. Envelopes always carry an
                // "event" field; the ScanResult never does. Walk stdout backward for the last JSON
                // object WITHOUT an "event" field so a truncated or duplicated trailing envelope is
                // never mis-parsed as the result (that would deserialize to Success=true with zero
                // processed/evicted counts and let the caller false-complete the scan).
                foreach (var line in result.Output
                             .Split('\n')
                             .Select(l => l.TrimEnd('\r'))
                             .Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        var element = System.Text.Json.JsonSerializer.Deserialize<JsonElement>(line);
                        if (element.ValueKind == JsonValueKind.Object && !element.TryGetProperty("event", out _))
                        {
                            return new RustExecutionResult { Success = true, Data = element, Error = null };
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse cache_eviction_scan stdout JSON line: {Line}", line);
                    }
                }

                // Exit 0 but no ScanResult line (empty/truncated stdout, or only progress
                // envelopes): treat as failure so the caller cannot complete the scan with zeroed
                // metrics as though it had succeeded.
                _logger.LogWarning("[cache_eviction_scan] Exited 0 but produced no ScanResult line; treating as scan failure");
                return new RustExecutionResult
                {
                    Success = false,
                    Data = null,
                    Error = "cache_eviction_scan exited successfully but produced no scan result line"
                };
            }

            return new RustExecutionResult
            {
                Success = false,
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

    /// <summary>
    /// Central Rust-failure gate: no-op when <see cref="ExitCode"/> is 0, otherwise throws a typed
    /// <see cref="RustProcessException"/> carrying the exit code and stderr (<see cref="Error"/>).
    /// Replaces the scattered <c>if (result.ExitCode != 0) throw new Exception($"... exit code ...")</c>
    /// checks so every Rust wrapper fails the same way and callers can <c>catch (RustProcessException)</c>.
    /// </summary>
    /// <param name="tool">The Rust binary/tool name for the message (e.g. "cache_cleaner").</param>
    /// <param name="context">Optional user-safe descriptor (e.g. datasource/service name); never stderr.</param>
    public void EnsureSuccess(string tool, string? context = null)
    {
        if (ExitCode == 0)
        {
            return;
        }

        throw new RustProcessException(tool, ExitCode, Error, context);
    }
}

/// <summary>
/// Strongly-typed envelope for the JSON lines Rust's progress_events.rs ProgressReporter emits
/// over stdout (event types "started"/"progress"/"complete" — see that file's header comment for
/// the exact wire shape). PercentComplete is only populated on "progress" events; Success/
/// Cancelled are only populated on "complete" events — the other event types leave them null.
/// Context is a free-form JSON object whose shape varies by StageKey (Rust declares it as
/// serde_json::Value, genuinely heterogeneous even within one binary's stream), so it stays a
/// JsonElement rather than being forced into a fixed shape; callers that need specific fields out
/// of it read them via Context.Value.TryGetProperty(...).
/// </summary>
public class RustProgressEvent
{
    [System.Text.Json.Serialization.JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("operationId")]
    public string OperationId { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double? PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string StageKey { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public JsonElement? Context { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool? Success { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("cancelled")]
    public bool? Cancelled { get; set; }

    /// <summary>
    /// The full failure reason for "failed"/"complete" events that carry one — Rust's
    /// progress_events.rs emits the whole anyhow chain (<c>format!("{e:#}")</c>) here. Null on
    /// success and on any envelope emitted before the errorDetail field was added, so parsing must
    /// tolerate its absence (it does — nullable + case-insensitive deserialization).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("errorDetail")]
    public string? ErrorDetail { get; set; }
}

/// <summary>
/// Result of a Rust executable execution with data result
/// </summary>
public class RustExecutionResult
{
    public bool Success { get; set; }
    public int ExitCode { get; set; }
    public object? Data { get; set; }
    public string? Error { get; set; }
}


/// <summary>Final line/file totals produced by log_service_manager count-lines.</summary>
public sealed record LogLineCountResult(long LinesProcessed, long FilesProcessed);

/// <summary>Pre-delete byte count produced by log_service_manager delete-file.</summary>
public sealed record LogFileDeletionResult(long BytesDeleted);
