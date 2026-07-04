using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Docker-inspect-first, <c>.env</c>-file-second tiered environment value lookup (contract
/// amendment v1.2). Merges <c>Config.Env</c> from the running lancache-dns container with the
/// cache/monolithic container as a per-key fallback (DNS wins on overlap - these variables
/// typically live on the DNS container's compose service), cached briefly to avoid a Docker API
/// round trip per key per sweep/dropdown request.
/// </summary>
public sealed class LancacheEnvironmentSource : ILancacheEnvironmentSource
{
    private static readonly TimeSpan _dockerEnvCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ILogger<LancacheEnvironmentSource> _logger;
    private readonly ILancacheEnvFileReader _envReader;
    private readonly DockerClient? _dockerClient;

    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private Dictionary<string, string>? _cachedDockerEnv;
    private DateTime _cachedDockerEnvAtUtc = DateTime.MinValue;

    public LancacheEnvironmentSource(
        ILogger<LancacheEnvironmentSource> logger,
        ILancacheEnvFileReader envReader)
    {
        _logger = logger;
        _envReader = envReader;

        try
        {
            if (!OperatingSystemDetector.IsWindows && File.Exists("/var/run/docker.sock"))
            {
                _dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: Docker client not available for tiered env lookup - falling back to .env file only");
        }
    }

    public async Task<EnvValueResult> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        var dockerValue = await TryGetFromDockerAsync(key, cancellationToken);
        if (dockerValue != null)
        {
            return new EnvValueResult { Value = dockerValue, Source = EnvValueSource.DockerInspect };
        }

        return new EnvValueResult { Value = _envReader.TryGetValue(key), Source = EnvValueSource.EnvFile };
    }

    private async Task<string?> TryGetFromDockerAsync(string key, CancellationToken ct)
    {
        if (_dockerClient == null)
        {
            return null;
        }

        var env = await GetMergedDockerEnvAsync(ct);
        return env.TryGetValue(key, out var value) ? value : null;
    }

    private async Task<Dictionary<string, string>> GetMergedDockerEnvAsync(CancellationToken ct)
    {
        await _cacheLock.WaitAsync(ct);
        try
        {
            if (_cachedDockerEnv != null && DateTime.UtcNow - _cachedDockerEnvAtUtc < _dockerEnvCacheTtl)
            {
                return _cachedDockerEnv;
            }

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var containers = await _dockerClient!.Containers.ListContainersAsync(
                    new ContainersListParameters { All = false }, ct);

                // Cache/monolithic container merged FIRST (lower priority - fills gaps); the
                // lancache-dns container merged SECOND so its values win on any key overlap.
                var cacheContainer = containers.FirstOrDefault(c =>
                    !DockerContainerMatching.IsManagerContainer(c.Names ?? new List<string>(), c.Image ?? string.Empty) &&
                    !DockerContainerMatching.IsDnsContainer(c.Names ?? new List<string>()) &&
                    DockerContainerMatching.IsLancacheCacheContainer(c.Image ?? string.Empty, c.Names ?? new List<string>()));
                if (cacheContainer != null)
                {
                    await MergeContainerEnvAsync(merged, cacheContainer.ID, ct);
                }

                var dnsContainer = containers.FirstOrDefault(c => DockerContainerMatching.IsDnsContainer(c.Names ?? new List<string>()));
                if (dnsContainer != null)
                {
                    await MergeContainerEnvAsync(merged, dnsContainer.ID, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Status Check: failed to read Docker container env for tiered env lookup");
            }

            _cachedDockerEnv = merged;
            _cachedDockerEnvAtUtc = DateTime.UtcNow;
            return merged;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task MergeContainerEnvAsync(Dictionary<string, string> merged, string containerId, CancellationToken ct)
    {
        try
        {
            var inspect = await _dockerClient!.Containers.InspectContainerAsync(containerId, ct);
            var envList = inspect.Config?.Env;
            if (envList == null)
            {
                return;
            }

            foreach (var entry in envList)
            {
                var separatorIndex = entry.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                merged[entry[..separatorIndex]] = entry[(separatorIndex + 1)..];
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: failed to inspect container {ContainerId} for env vars", containerId);
        }
    }
}
