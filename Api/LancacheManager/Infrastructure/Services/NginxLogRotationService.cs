using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Action that can make nginx log reopen available to the manager.
/// </summary>
[JsonConverter(typeof(NginxReopenHintJsonConverter))]
public enum NginxReopenHint
{
    None,
    GrantSignalPrivilege,
    EnablePidHost,
    MountDockerSocket
}

internal sealed class NginxReopenHintJsonConverter : JsonStringEnumConverter<NginxReopenHint>
{
    public NginxReopenHintJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Current nginx reopen availability and the applicable remedy when unavailable.
/// </summary>
public sealed record NginxReopenAvailability(bool Available, NginxReopenHint Hint);

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

    private const string HostPidVisibleMarker = "nginx-pid-visible";
    private const int HostPidNotVisibleExitCode = 3;
    private const string NoNginxContainerFoundError = "No container with nginx found";
    private const string HostNginxPidExpression =
        "$(cat /run/nginx.pid 2>/dev/null || cat /var/run/nginx.pid 2>/dev/null || " +
        "pgrep -f 'nginx[:] master' | head -1)";

    private readonly ILogger<NginxLogRotationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProcessManager _processManager;
    private readonly IPathResolver _pathResolver;
    private readonly TimeProvider _timeProvider;
    private readonly object _bareMetalWarningLock = new();
    private readonly object _availabilityCacheLock = new();
    private readonly SemaphoreSlim _availabilityProbeLock = new(1, 1);
    private DateTimeOffset? _lastBareMetalWarning;
    private AvailabilityCacheEntry? _availability;

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
    /// and then the host signal path. The combined result is cached briefly because probing may
    /// spawn processes.
    /// </summary>
    public async Task<bool> CanReopenNginxAsync()
    {
        try
        {
            return (await GetCachedAvailabilityAsync()).Available;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability check failed");
            return false;
        }
    }

    /// <summary>
    /// Returns nginx reopen availability plus the detected remedy for a datasource layout.
    /// Probe evidence is shared with <see cref="CanReopenNginxAsync"/> and cached for the same TTL.
    /// </summary>
    public async Task<NginxReopenAvailability> GetNginxReopenAvailabilityAsync(string? datasourceLayout)
    {
        try
        {
            var detection = await GetCachedAvailabilityAsync();
            if (detection.Available)
            {
                return new NginxReopenAvailability(true, NginxReopenHint.None);
            }

            return new NginxReopenAvailability(
                false,
                detection.DetectedHint ?? GetLayoutFallbackHint(datasourceLayout));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability check failed");
            return new NginxReopenAvailability(false, GetLayoutFallbackHint(datasourceLayout));
        }
    }

    private async Task<AvailabilityDetection> GetCachedAvailabilityAsync()
    {
        if (TryGetCachedAvailability(out var cached))
        {
            return cached;
        }

        await _availabilityProbeLock.WaitAsync();
        try
        {
            if (TryGetCachedAvailability(out cached))
            {
                return cached;
            }

            var detection = AvailabilityDetection.Unavailable();
            try
            {
                if (_configuration.GetValue<bool>("NginxLogRotation:Enabled", false))
                {
                    detection = await DetectAvailabilityAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nginx reopen availability detection failed");
            }

            lock (_availabilityCacheLock)
            {
                _availability = new AvailabilityCacheEntry(detection, _timeProvider.GetUtcNow());
            }

            return detection;
        }
        finally
        {
            _availabilityProbeLock.Release();
        }
    }

    private bool TryGetCachedAvailability(out AvailabilityDetection detection)
    {
        lock (_availabilityCacheLock)
        {
            if (_availability is not null &&
                _timeProvider.GetUtcNow() - _availability.CheckedAt < _availabilityCacheTtl)
            {
                detection = _availability.Detection;
                return true;
            }
        }

        detection = AvailabilityDetection.Unavailable();
        return false;
    }

    private async Task<AvailabilityDetection> DetectAvailabilityAsync()
    {
        DockerProbeResult dockerProbe;
        try
        {
            dockerProbe = await ProbeDockerReopenAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability probe failed for Docker");
            dockerProbe = DockerProbeResult.Unknown;
        }

        if (dockerProbe.Available)
        {
            return AvailabilityDetection.AvailableResult;
        }

        HostProbeResult hostProbe;
        try
        {
            hostProbe = await ProbeHostSignalAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nginx reopen availability probe failed for host nginx");
            hostProbe = HostProbeResult.Unknown;
        }

        if (hostProbe.Available)
        {
            return AvailabilityDetection.AvailableResult;
        }

        if (hostProbe.Failure == HostProbeFailure.SignalDenied)
        {
            return AvailabilityDetection.Unavailable(NginxReopenHint.GrantSignalPrivilege);
        }

        if (dockerProbe.Failure == DockerProbeFailure.NoNginxContainer &&
            hostProbe.Failure == HostProbeFailure.PidNotVisible)
        {
            return AvailabilityDetection.Unavailable(NginxReopenHint.EnablePidHost);
        }

        return AvailabilityDetection.Unavailable();
    }

    private async Task<DockerProbeResult> ProbeDockerReopenAsync()
    {
        if (!_pathResolver.IsDockerSocketAvailable())
        {
            return new DockerProbeResult(false, DockerProbeFailure.SocketMissing);
        }

        var configuredName = _configuration.GetValue<string>("NginxLogRotation:ContainerName");
        if (!string.IsNullOrWhiteSpace(configuredName) &&
            !string.Equals(configuredName, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return await ContainerHasNginxAsync(configuredName)
                ? DockerProbeResult.AvailableResult
                : DockerProbeResult.Unknown;
        }

        var (containerName, error) = await FindMonolithicContainerAsync();
        if (!string.IsNullOrWhiteSpace(containerName))
        {
            return DockerProbeResult.AvailableResult;
        }

        if (string.Equals(error, NoNginxContainerFoundError, StringComparison.Ordinal))
        {
            return new DockerProbeResult(false, DockerProbeFailure.NoNginxContainer);
        }

        if (error?.Contains("Docker socket", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new DockerProbeResult(false, DockerProbeFailure.SocketMissing);
        }

        return DockerProbeResult.Unknown;
    }

    private async Task<HostProbeResult> ProbeHostSignalAsync()
    {
        var result = await RunProcessAsync(
            CreateHostProbeStartInfo(),
            "host nginx signal probe");
        if (result.ExitCode == 0)
        {
            return HostProbeResult.AvailableResult;
        }

        if (result.ExitCode == HostPidNotVisibleExitCode ||
            string.IsNullOrWhiteSpace(result.Error) ||
            result.Error.Contains("no process", StringComparison.OrdinalIgnoreCase) ||
            result.Error.Contains("No such process", StringComparison.OrdinalIgnoreCase))
        {
            return new HostProbeResult(false, HostProbeFailure.PidNotVisible);
        }

        if (IndicatesSignalPermissionDenied(result.Error))
        {
            return new HostProbeResult(false, HostProbeFailure.SignalDenied);
        }

        return HostProbeResult.Unknown;
    }

    private static bool IndicatesSignalPermissionDenied(string error) =>
        error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

    private static NginxReopenHint GetLayoutFallbackHint(string? datasourceLayout) =>
        datasourceLayout is LogSourceLayout.LayoutBareMetal or LogSourceLayout.LayoutMixed
            ? NginxReopenHint.EnablePidHost
            : NginxReopenHint.MountDockerSocket;

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

                    if (hostSignalResult.ExitCode == HostPidNotVisibleExitCode)
                    {
                        const string visibilityFailure =
                            "host nginx is not visible to the manager; enable pid: host and run " +
                            "the manager as root or with CAP_KILL";
                        var visibilityDetectionContext =
                            detectionError ?? "No LANCache container was found";
                        LogBareMetalFailure($"{visibilityDetectionContext}; {visibilityFailure}");
                        return LogRotationResult.Failed(
                            $"Failed to signal host nginx to reopen logs: {visibilityFailure}",
                            false);
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
                "sh", "-c", "kill -USR1 $(cat /var/run/nginx.pid 2>/dev/null || pgrep -f 'nginx[:] master' | head -1)");

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
                // Sidecars that share the "lancache" name prefix but never run nginx: the
                // manager UI, the DNS resolver, and the database backend (e.g. lancache-db
                // on a postgres image). Excluding them keeps them out of the nginx search.
                return lower.Contains("dns")
                    || lower.Contains("manager")
                    || lower.Contains("postgres")
                    || lower.Contains("redis")
                    || lower.Contains("mariadb")
                    || lower.Contains("mysql");
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
                // A single "lancache"-named match is ambiguous: a database or other sidecar
                // can share the prefix, so confirm nginx is actually present before claiming
                // it as the reopen target. If it is not, keep searching for a real nginx host.
                if (await ContainerHasNginxAsync(match.Name))
                {
                    _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", match.Name);
                    return (match.Name, null);
                }

                _logger.LogDebug(
                    "Sole lancache-named container {ContainerName} has no nginx; continuing search.",
                    match.Name);
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

            // Fall back to any container with nginx installed, but still skip the known
            // sidecars so a manager/dns/database container is never picked as the target.
            foreach (var container in containers)
            {
                if (LooksLikeNonCache(container.Name) || LooksLikeNonCache(container.Image)) continue;

                var hasNginx = await ContainerHasNginxAsync(container.Name);
                if (!hasNginx) continue;

                // Found a container with nginx
                _logger.LogInformation("Found container with nginx: {ContainerName}", container.Name);
                return (container.Name, null);
            }

            // No suitable container found

            _logger.LogDebug("No suitable nginx container found; trying host nginx signaling");
            return (null, NoNginxContainerFoundError);
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

    /// <summary>
    /// Shell prefix shared by the host probe and signal scripts: resolves the host nginx pid and
    /// exits with <see cref="HostPidNotVisibleExitCode"/> before either script acts on it, so both
    /// stay in lockstep instead of drifting the way the probe-only guard once did.
    /// </summary>
    private static string BuildHostPidResolutionScript() =>
        $"pid={HostNginxPidExpression}; " +
        $"if [ -z \"$pid\" ]; then exit {HostPidNotVisibleExitCode}; fi; ";

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
        startInfo.ArgumentList.Add(
            BuildHostPidResolutionScript() +
            $"kill -{signal} \"$pid\"");
        return startInfo;
    }

    private static ProcessStartInfo CreateHostProbeStartInfo()
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
        startInfo.ArgumentList.Add(
            BuildHostPidResolutionScript() +
            $"printf '{HostPidVisibleMarker}\\n'; kill -0 \"$pid\"");
        return startInfo;
    }

    /// <summary>
    /// Test seam for commands that production runs through the shared process manager.
    /// </summary>
    protected virtual Task<ProcessCommandResult> RunProcessAsync(ProcessStartInfo startInfo, string label) =>
        _processManager.RunAsync(startInfo, label: label);

    private enum DockerProbeFailure
    {
        None,
        SocketMissing,
        NoNginxContainer,
        Unknown
    }

    private enum HostProbeFailure
    {
        None,
        PidNotVisible,
        SignalDenied,
        Unknown
    }

    private sealed record DockerProbeResult(bool Available, DockerProbeFailure Failure)
    {
        public static DockerProbeResult AvailableResult { get; } = new(true, DockerProbeFailure.None);
        public static DockerProbeResult Unknown { get; } = new(false, DockerProbeFailure.Unknown);
    }

    private sealed record HostProbeResult(bool Available, HostProbeFailure Failure)
    {
        public static HostProbeResult AvailableResult { get; } = new(true, HostProbeFailure.None);
        public static HostProbeResult Unknown { get; } = new(false, HostProbeFailure.Unknown);
    }

    private sealed record AvailabilityDetection(bool Available, NginxReopenHint? DetectedHint)
    {
        public static AvailabilityDetection AvailableResult { get; } = new(true, null);

        public static AvailabilityDetection Unavailable(NginxReopenHint? hint = null) =>
            new(false, hint);
    }

    private sealed record AvailabilityCacheEntry(
        AvailabilityDetection Detection,
        DateTimeOffset CheckedAt);
}
