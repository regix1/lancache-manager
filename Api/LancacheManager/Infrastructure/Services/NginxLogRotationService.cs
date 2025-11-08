using System.Diagnostics;

namespace LancacheManager.Infrastructure.Services;

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
    public async Task<bool> ReopenNginxLogsAsync()
    {
        try
        {
            var enabled = _configuration.GetValue<bool>("NginxLogRotation:Enabled", false);

            if (!enabled)
            {
                _logger.LogDebug("Nginx log rotation is disabled in configuration");
                return false;
            }

            // Try to auto-detect container name if not explicitly configured
            var configuredName = _configuration.GetValue<string>("NginxLogRotation:ContainerName");
            var containerName = !string.IsNullOrEmpty(configuredName)
                ? configuredName
                : await DetectMonolithicContainerAsync();

            if (string.IsNullOrEmpty(containerName))
            {
                _logger.LogWarning("Could not find monolithic container. Set NginxLogRotation:ContainerName in appsettings.json to specify container name.");
                return false;
            }

            _logger.LogInformation("Sending nginx log reopen signal to container: {ContainerName}", containerName);

            // Try nginx -s reopen first (preferred method)
            var reopenSuccess = await ExecuteDockerCommandAsync(containerName, "nginx -s reopen");

            if (reopenSuccess)
            {
                _logger.LogInformation("Successfully signaled nginx to reopen logs in {ContainerName}", containerName);
                return true;
            }

            // Fallback: Try sending SIGUSR1 to nginx master process
            _logger.LogInformation("nginx -s reopen failed, attempting SIGUSR1 signal...");
            var killSuccess = await ExecuteDockerCommandAsync(
                containerName,
                "sh -c \"kill -USR1 $(cat /var/run/nginx.pid || pgrep -f 'nginx: master')\"");

            if (killSuccess)
            {
                _logger.LogInformation("Successfully sent SIGUSR1 to nginx in {ContainerName}", containerName);
                return true;
            }

            _logger.LogWarning("Failed to signal nginx to reopen logs in {ContainerName}", containerName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while attempting to signal nginx log rotation");
            return false;
        }
    }

    /// <summary>
    /// Auto-detect the monolithic container by looking for containers with nginx
    /// and matching volume mounts for /data/logs and /data/cache
    /// </summary>
    private async Task<string?> DetectMonolithicContainerAsync()
    {
        try
        {
            _logger.LogDebug("Attempting to auto-detect monolithic container...");

            // Check if docker socket is accessible
            if (!File.Exists("/var/run/docker.sock"))
            {
                _logger.LogWarning("Docker socket not found at /var/run/docker.sock. " +
                    "Mount the docker socket to enable nginx log rotation: " +
                    "volumes: - /var/run/docker.sock:/var/run/docker.sock:ro");
                return null;
            }

            // Get all running containers with nginx
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "ps --filter status=running --format \"{{.Names}}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start docker ps command. Docker socket may not be mounted.");
                return null;
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var errorMsg = string.IsNullOrWhiteSpace(stderr) ? "Unknown error" : stderr.Trim();
                _logger.LogWarning("docker ps command failed with exit code {ExitCode}: {Error}. " +
                    "Docker socket may not be mounted or accessible.", process.ExitCode, errorMsg);
                return null;
            }

            var containers = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Look for containers with "monolithic", "lancache", or "nginx" in the name
            foreach (var container in containers)
            {
                var containerName = container.Trim();
                if (string.IsNullOrEmpty(containerName)) continue;

                // Check if this container has nginx
                var hasNginx = await CheckContainerHasNginxAsync(containerName);
                if (!hasNginx) continue;

                // Found a container with nginx
                var nameLower = containerName.ToLowerInvariant();
                if (nameLower.Contains("monolithic") || nameLower.Contains("lancache"))
                {
                    _logger.LogInformation("Auto-detected monolithic container: {ContainerName}", containerName);
                    return containerName;
                }
            }

            // If no name match, try the first container with nginx (last resort)
            foreach (var container in containers)
            {
                var containerName = container.Trim();
                if (string.IsNullOrEmpty(containerName)) continue;

                var hasNginx = await CheckContainerHasNginxAsync(containerName);
                if (hasNginx)
                {
                    _logger.LogInformation("Found container with nginx (no name match): {ContainerName}", containerName);
                    return containerName;
                }
            }

            _logger.LogWarning("No suitable container found for nginx log rotation");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting monolithic container");
            return null;
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

    private async Task<bool> ExecuteDockerCommandAsync(string containerName, string command)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                _logger.LogWarning("Failed to start docker process");
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    _logger.LogDebug("Docker command output: {Output}", stdout.Trim());
                }
                return true;
            }
            else
            {
                _logger.LogWarning("Docker command failed with exit code {ExitCode}: {Error}",
                    process.ExitCode, stderr.Trim());
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing docker command: {Command}", command);
            return false;
        }
    }
}
