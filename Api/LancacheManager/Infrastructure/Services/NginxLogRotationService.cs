using System.Diagnostics;

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
/// This prevents the monolithic container from losing access to access.log
/// </summary>
public class NginxLogRotationService
{
    private readonly ILogger<NginxLogRotationService> _logger;
    private readonly IConfiguration _configuration;

    public NginxLogRotationService(
        ILogger<NginxLogRotationService> logger,
        IConfiguration _configuration)
    {
        _logger = logger;
        this._configuration = _configuration;
    }

    /// <summary>
    /// Signals nginx in the monolithic container to reopen log files
    /// Auto-detects the container or uses configured name
    /// </summary>
    public async Task<LogRotationResult> ReopenNginxLogsAsync()
    {
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
                : await DetectMonolithicContainerAsync();

            if (string.IsNullOrEmpty(containerName))
            {
                var errorMsg = detectionError ?? "Could not find monolithic container";
                _logger.LogWarning("{Error}. Set NginxLogRotation:ContainerName in appsettings.json to specify container name.", errorMsg);
                return LogRotationResult.Failed(errorMsg, detectionError?.Contains("Docker socket") == true);
            }

            _logger.LogInformation("Signaling nginx to reopen logs in container: {ContainerName}", containerName);

            // Try method 1: Send USR1 to PID 1 (works if nginx is the main process)
            var directSignalSuccess = await SendSignalToContainerAsync(containerName, "USR1");
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
            _logger.LogError(ex, "Error while attempting to signal nginx log rotation");
            return LogRotationResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Auto-detect the monolithic container by matching name/image hints,
    /// with nginx checks as a fallback when there are multiple candidates.
    /// </summary>
    /// <returns>A tuple of (container name, error message). If container is found, error is null.</returns>
    private async Task<(string? ContainerName, string? Error)> DetectMonolithicContainerAsync()
    {
        try
        {
            _logger.LogDebug("Attempting to auto-detect monolithic container...");

            // Check if docker socket is accessible
            if (!File.Exists("/var/run/docker.sock"))
            {
                var error = "Docker socket not mounted. Add /var/run/docker.sock:/var/run/docker.sock to your volumes.";
                _logger.LogWarning("Docker socket not found at /var/run/docker.sock. " +
                    "Mount the docker socket to enable nginx log rotation: " +
                    "volumes: - /var/run/docker.sock:/var/run/docker.sock");
                return (null, error);
            }

            // Get all running containers with names and images
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "ps --filter status=running --format \"{{.Names}}|{{.Image}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                var error = "Docker command failed. Ensure Docker socket is mounted.";
                _logger.LogWarning("Failed to start docker ps command. Docker socket may not be mounted.");
                return (null, error);
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var errorMsg = string.IsNullOrWhiteSpace(stderr) ? "Unknown error" : stderr.Trim();
                _logger.LogWarning("docker ps command failed with exit code {ExitCode}: {Error}. " +
                    "Docker socket may not be mounted or accessible.", process.ExitCode, errorMsg);
                return (null, $"Docker command failed: {errorMsg}");
            }

            static bool LooksLikeLancache(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                var lower = value.ToLowerInvariant();
                return lower.Contains("lancache") || lower.Contains("monolithic");
            }

            static bool LooksLikeMonolithic(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                return value.ToLowerInvariant().Contains("monolithic");
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
                    var hasNginx = await CheckContainerHasNginxAsync(candidate.Name);
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
                    var hasNginx = await CheckContainerHasNginxAsync(candidate.Name);
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
                var hasNginx = await CheckContainerHasNginxAsync(container.Name);
                if (!hasNginx) continue;

                // Found a container with nginx
                _logger.LogInformation("Found container with nginx: {ContainerName}", container.Name);
                return (container.Name, null);
            }

            // No suitable container found

            _logger.LogWarning("No suitable container found for nginx log rotation");
            return (null, "No container with nginx found");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting monolithic container");
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Check if a container has nginx by trying to execute nginx -v
    /// </summary>
    private async Task<bool> CheckContainerHasNginxAsync(string containerName)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} sh -c \"which nginx || command -v nginx\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
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
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} {commandStr}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start docker exec process");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                _logger.LogDebug("Command executed successfully in container {Container}", containerName);
                return true;
            }
            else
            {
                _logger.LogDebug("docker exec failed with exit code {ExitCode}: {Error}",
                    process.ExitCode, stderr.Trim());
                return false;
            }
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
    private async Task<bool> SendSignalToContainerAsync(string containerName, string signal)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"kill --signal={signal} {containerName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start docker kill process");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                _logger.LogDebug("Signal {Signal} sent successfully to container {Container}", signal, containerName);
                return true;
            }
            else
            {
                _logger.LogWarning("docker kill failed with exit code {ExitCode}: {Error}",
                    process.ExitCode, stderr.Trim());
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending signal {Signal} to container {Container}", signal, containerName);
            return false;
        }
    }
}
