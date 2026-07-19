using System.Diagnostics;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Result of a log rotation operation
/// </summary>
public class LogRotationResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public bool DockerSocketMissing { get; set; }

    public static LogRotationResult Succeeded() => new() { Success = true };
    public static LogRotationResult Failed(string message, bool dockerSocketMissing = false) => new()
    {
        Success = false,
        ErrorMessage = message,
        DockerSocketMissing = dockerSocketMissing
    };
}

/// <summary>
/// Service to signal nginx to reopen log files after log manipulation operations
/// This prevents containerized and bare-metal nginx from losing access to rewritten logs
/// </summary>
public class NginxLogRotationService
{
    private static readonly TimeSpan _bareMetalWarningThrottle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _availabilityCacheTtl = TimeSpan.FromSeconds(30);

    private const string HostNginxPidExpression =
        "$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || " +
        "pgrep -f 'nginx: master' | head -1)";

    private readonly ILogger<NginxLogRotationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProcessManager _processManager;
    private readonly IPathResolver _pathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly object _bareMetalWarningLock = new();
    private readonly object _availabilityCacheLock = new();
    private readonly SemaphoreSlim _availabilityProbeLock = new(1, 1);
    private DateTimeOffset? _lastBareMetalWarning;
    private AvailabilityCacheEntry? _dockerAvailability;
    private AvailabilityCacheEntry? _hostAvailability;

    public NginxLogRotationService(
        ILogger<NginxLogRotationService> logger,
        IConfiguration configuration,
        ProcessManager processManager,
        IPathResolver pathResolver)
        : this(logger, configuration, processManager, pathResolver, TimeProvider.System)
    {
    }

    internal NginxLogRotationService(
        ILogger<NginxLogRotationService> logger,
        IConfiguration configuration,
        ProcessManager processManager,
        IPathResolver pathResolver,
        TimeProvider timeProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _processManager = processManager;
        _pathResolver = pathResolver;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns whether either reopen path is currently usable, checking the container path first
    /// and then the host signal path. Results for each path are cached briefly because probing may
    /// spawn processes.
    /// </summary>
    public async Task<bool> CanReopenNginxAsync()
    {
        return await GetCachedAvailabilityAsync(ReopenTarget.Docker) ||
               await GetCachedAvailabilityAsync(ReopenTarget.Host);
    }

    private async Task<bool> GetCachedAvailabilityAsync(ReopenTarget target)
    {
        if (TryGetCachedAvailability(target, out var cached))
        {
            return cached;
        }

        await _availabilityProbeLock.WaitAsync();
        try
        {
            if (TryGetCachedAvailability(target, out cached))
            {
                return cached;
            }

            var available = false;
            try
            {
                if (_configuration.GetValue<bool>("NginxLogRotation:Enabled", false))
                {
                    available = target == ReopenTarget.Docker
                        ? await ProbeDockerReopenAsync()
                        : await ProbeHostSignalAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nginx reopen availability probe failed for {Target}", target);
            }

            lock (_availabilityCacheLock)
            {
                var entry = new AvailabilityCacheEntry(available, _timeProvider.GetUtcNow());
                if (target == ReopenTarget.Docker)
                {
                    _dockerAvailability = entry;
                }
                else
                {
                    _hostAvailability = entry;
                }
            }

            return available;
        }
        finally
        {
            _availabilityProbeLock.Release();
        }
    }

    private bool TryGetCachedAvailability(ReopenTarget target, out bool available)
    {
        lock (_availabilityCacheLock)
        {
            var entry = target == ReopenTarget.Docker
                ? _dockerAvailability
                : _hostAvailability;
            if (entry is not null &&
                _timeProvider.GetUtcNow() - entry.CheckedAt < _availabilityCacheTtl)
            {
                available = entry.Available;
                return true;
            }
        }

        available = false;
        return false;
    }

    private async Task<bool> ProbeDockerReopenAsync()
    {
        if (!_pathResolver.IsDockerSocketAvailable())
        {
            return false;
        }

        var configuredName = _configuration.GetValue<string>("NginxLogRotation:ContainerName");
        if (!string.IsNullOrWhiteSpace(configuredName) &&
            !string.Equals(configuredName, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return await ContainerHasNginxAsync(configuredName);
        }

        var (containerName, _) = await FindMonolithicContainerAsync();
        return !string.IsNullOrWhiteSpace(containerName);
    }

    private async Task<bool> ProbeHostSignalAsync()
    {
        var result = await RunProcessAsync(
            CreateHostSignalStartInfo("0"),
            "host nginx signal probe");
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Signals nginx to reopen log files. A configured or auto-detected LANCache container is
    /// preferred; when none is found, the host nginx master is signaled locally.
    /// </summary>
    public async Task<LogRotationResult> ReopenNginxLogsAsync()
    {
        void LogBareMetalFailure(string failureReason, Exception? exception = null)
        {
            var shouldLog = false;
            lock (_bareMetalWarningLock)
            {
                var now = _timeProvider.GetUtcNow();
                if (!_lastBareMetalWarning.HasValue ||
                    now - _lastBareMetalWarning.Value >= _bareMetalWarningThrottle)
                {
                    _lastBareMetalWarning = now;
                    shouldLog = true;
                }
            }

            if (!shouldLog)
            {
                return;
            }

            const string message =
                "Bare-metal nginx log reopen failed: {FailureReason}. Run the manager with " +
                "--pid=host and as root or with CAP_KILL, or configure the host's logrotate " +
                "to run 'nginx -s reopen' after rotation.";

            if (exception is null)
            {
                _logger.LogWarning(message, failureReason);
            }
            else
            {
                _logger.LogWarning(exception, message, failureReason);
            }
        }

        try
        {
            var enabled = _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

            if (!enabled)
            {
                _logger.LogDebug("Nginx log rotation is disabled in configuration");
                return LogRotationResult.Failed("Log rotation is disabled in configuration");
            }

            // Try to auto-detect container name if not explicitly configured
            var configuredName = _configuration.GetValue<string>("NginxLogRotation:ContainerName");
            var (containerName, detectionError) =
                !string.IsNullOrEmpty(configuredName) &&
                !string.Equals(configuredName, "auto", StringComparison.OrdinalIgnoreCase)
                ? (configuredName, (string?)null)
                : await FindMonolithicContainerAsync();

            if (string.IsNullOrEmpty(containerName))
            {
                try
                {
                    var hostSignalResult = await RunProcessAsync(
                        CreateHostSignalStartInfo("USR1"),
                        "host nginx log reopen");

                    if (hostSignalResult.ExitCode == 0)
                    {
                        lock (_bareMetalWarningLock)
                        {
                            _lastBareMetalWarning = null;
                        }

                        _logger.LogInformation("Successfully sent USR1 to the host nginx master process");
                        return LogRotationResult.Succeeded();
                    }

                    var processFailure = string.IsNullOrWhiteSpace(hostSignalResult.Error)
                        ? $"command exited with code {hostSignalResult.ExitCode}"
                        : $"command exited with code {hostSignalResult.ExitCode}: {hostSignalResult.Error.Trim()}";
                    var detectionContext = detectionError ?? "No LANCache container was found";
                    LogBareMetalFailure($"{detectionContext}; {processFailure}");
                    return LogRotationResult.Failed(
                        $"Failed to signal host nginx to reopen logs: {processFailure}",
                        detectionError?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true);
                }
                catch (Exception ex)
                {
                    var processFailure = $"could not run the local signal command: {ex.Message}";
                    var detectionContext = detectionError ?? "No LANCache container was found";
                    LogBareMetalFailure($"{detectionContext}; {processFailure}", ex);
                    return LogRotationResult.Failed(
                        $"Failed to signal host nginx to reopen logs: {processFailure}",
                        detectionError?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true);
                }
            }

            _logger.LogInformation("Signaling nginx to reopen logs in container: {ContainerName}", containerName);

            // Try method 1: Send USR1 to PID 1 (works if nginx is the main process)
            var directSignalSuccess = await SignalContainerAsync(containerName, "USR1");
            if (directSignalSuccess)
            {
                _logger.LogInformation("Successfully sent USR1 to PID 1 in {ContainerName}", containerName);
            }

            // Try method 2: Execute kill command inside container to find and signal nginx master process
            // This works even when nginx is not PID 1 (e.g., running under supervisor)
            var execSuccess = await ExecuteInContainerAsync(containerName,
                "sh", "-c", "kill -USR1 $(cat /var/run/nginx.pid 2>/dev/null || pgrep -f 'nginx: master' | head -1)");

            if (execSuccess)
            {
                _logger.LogInformation("Successfully sent USR1 to nginx master process in {ContainerName}", containerName);
                return LogRotationResult.Succeeded();
            }

            if (directSignalSuccess)
            {
                // First method worked, assume it was successful
                return LogRotationResult.Succeeded();
            }

            _logger.LogWarning("Failed to signal nginx to reopen logs in {ContainerName}", containerName);
            return LogRotationResult.Failed($"Failed to signal nginx in container '{containerName}'");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while attempting best-effort nginx log reopen");
            return LogRotationResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Auto-detect the monolithic container by matching name/image hints,
    /// with nginx checks as a fallback when there are multiple candidates.
    /// </summary>
    /// <returns>A tuple of (container name, error message). If container is found, error is null.</returns>
    protected virtual async Task<(string? ContainerName, string? Error)> FindMonolithicContainerAsync()
    {
        try
        {
            _logger.LogDebug("Attempting to auto-detect monolithic container...");

            // Check if docker socket is accessible
            if (!_pathResolver.IsDockerSocketAvailable())
            {
                var error = "Docker socket not mounted. Add /var/run/docker.sock:/var/run/docker.sock to your volumes.";
                _logger.LogDebug("Docker socket not found at /var/run/docker.sock; trying host nginx signaling");
                return (null, error);
            }

            // Get all running containers with names and images
            var processStartInfo = CreateDockerStartInfo(
                "ps --filter status=running --format \"{{.Names}}|{{.Image}}\"");

            var result = await RunProcessAsync(processStartInfo, "docker ps");

            if (result.ExitCode != 0)
            {
                var errorMsg = string.IsNullOrWhiteSpace(result.Error) ? "Unknown error" : result.Error.Trim();
                _logger.LogDebug(
                    "docker ps failed with exit code {ExitCode}: {Error}; trying host nginx signaling",
                    result.ExitCode,
                    errorMsg);
                return (null, $"Docker command failed: {errorMsg}");
            }

            var stdout = result.Output;

            static bool LooksLikeLancache(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var lower = value.ToLowerInvariant();
                return lower.Contains("lancache") || lower.Contains("monolithic");
            }

            static bool LooksLikeMonolithic(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                return value.Contains("monolithic", StringComparison.OrdinalIgnoreCase);
            }

            static bool LooksLikeNonCache(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var lower = value.ToLowerInvariant();
                return lower.Contains("dns") || lower.Contains("manager");
            }

            var containers = stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line))
                .Select(line =>
                {
                    var parts = line.Split('|', 2);
                    var name = parts[0].Trim();
                    var image = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    return (Name: name, Image: image);
                })
                .Where(container => !string.IsNullOrEmpty(container.Name))
                .ToList();

            var candidates = containers
                .Where(container => LooksLikeLancache(container.Name) || LooksLikeLancache(container.Image))
                .Where(container => !LooksLikeNonCache(container.Name) && !LooksLikeNonCache(container.Image))
                .ToList();

            var monolithicCandidates = candidates
                .Where(container => LooksLikeMonolithic(container.Name) || LooksLikeMonolithic(container.Image))
                .ToList();

            if (monolithicCandidates.Count == 1)
            {
                var match = monolithicCandidates[0];
                _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", match.Name);
                return (match.Name, null);
            }

            if (monolithicCandidates.Count > 1)
            {
                foreach (var candidate in monolithicCandidates)
                {
                    var hasNginx = await ContainerHasNginxAsync(candidate.Name);
                    if (hasNginx)
                    {
                        _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", candidate.Name);
                        return (candidate.Name, null);
                    }
                }

                var fallbackName = monolithicCandidates[0].Name;
                _logger.LogWarning("Multiple monolithic containers matched; using {ContainerName} without nginx validation.", fallbackName);
                return (fallbackName, null);
            }

            if (candidates.Count == 1)
            {
                var match = candidates[0];
                _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", match.Name);
                return (match.Name, null);
            }

            if (candidates.Count > 1)
            {
                foreach (var candidate in candidates)
                {
                    var hasNginx = await ContainerHasNginxAsync(candidate.Name);
                    if (hasNginx)
                    {
                        _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", candidate.Name);
                        return (candidate.Name, null);
                    }
                }

                var fallbackName = candidates[0].Name;
                _logger.LogWarning("Multiple containers matched lancache; using {ContainerName} without nginx validation.", fallbackName);
                return (fallbackName, null);
            }

            // Fall back to any container with nginx installed
            foreach (var container in containers)
            {
                var hasNginx = await ContainerHasNginxAsync(container.Name);
                if (!hasNginx) continue;

                // Found a container with nginx
                _logger.LogInformation("Found container with nginx: {ContainerName}", container.Name);
                return (container.Name, null);
            }

            // No suitable container found

            _logger.LogDebug("No suitable nginx container found; trying host nginx signaling");
            return (null, "No container with nginx found");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Container detection failed; trying host nginx signaling");
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Check if a container has nginx by trying to execute nginx -v
    /// </summary>
    private async Task<bool> ContainerHasNginxAsync(string containerName)
    {
        try
        {
            var processStartInfo = CreateDockerStartInfo(
                $"exec {containerName} sh -c \"which nginx || command -v nginx\"");

            var result = await RunProcessAsync(processStartInfo, "docker exec nginx-check");
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Execute a command inside a container using 'docker exec'
    /// </summary>
    private async Task<bool> ExecuteInContainerAsync(string containerName, params string[] command)
    {
        try
        {
            var commandStr = string.Join(" ", command.Select(c => c.Contains(" ") ? $"\"{c}\"" : c));
            var processStartInfo = CreateDockerStartInfo($"exec {containerName} {commandStr}");

            var result = await RunProcessAsync(processStartInfo, "docker exec");

            if (result.ExitCode == 0)
            {
                _logger.LogDebug("Command executed successfully in container {Container}", containerName);
                return true;
            }

            _logger.LogDebug("docker exec failed with exit code {ExitCode}: {Error}",
                result.ExitCode, result.Error.Trim());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error executing command in container {Container}", containerName);
            return false;
        }
    }

    /// <summary>
    /// Send a signal to a container using 'docker kill --signal'
    /// This sends signal to PID 1 in the container
    /// </summary>
    private async Task<bool> SignalContainerAsync(string containerName, string signal)
    {
        try
        {
            var processStartInfo = CreateDockerStartInfo($"kill --signal={signal} {containerName}");

            var result = await RunProcessAsync(processStartInfo, "docker kill");

            if (result.ExitCode == 0)
            {
                _logger.LogDebug("Signal {Signal} sent successfully to container {Container}", signal, containerName);
                return true;
            }

            _logger.LogWarning("docker kill failed with exit code {ExitCode}: {Error}",
                result.ExitCode, result.Error.Trim());
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending signal {Signal} to container {Container}", signal, containerName);
            return false;
        }
    }

    private static ProcessStartInfo CreateDockerStartInfo(string arguments) => new()
    {
        FileName = "docker",
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static ProcessStartInfo CreateHostSignalStartInfo(string signal)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"kill -{signal} {HostNginxPidExpression}");
        return startInfo;
    }

    /// <summary>
    /// Test seam for commands that production runs through the shared process manager.
    /// </summary>
    protected virtual Task<ProcessCommandResult> RunProcessAsync(ProcessStartInfo startInfo, string label) =>
        _processManager.RunAsync(startInfo, label: label);

    private enum ReopenTarget
    {
        Docker,
        Host
    }

    private sealed record AvailabilityCacheEntry(bool Available, DateTimeOffset CheckedAt);
}
