using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Locates the lancache HTTP cache server IP and the lancache-dns container IP, and probes
/// <c>/lancache-heartbeat</c>. Re-implements (does not modify) the equivalent private logic in
/// <c>PrefillDaemonServiceBase</c> (<c>ResolveLancacheServerIpAsync</c>/<c>DetectLancacheServerIpAsync</c>/
/// <c>GetLancacheDnsIpAsync</c>/<c>ProbeLancacheHeartbeatAsync</c>) - kept as a separate copy per the
/// swarm's deliberate risk-containment decision not to touch that 3700-line prefill-critical file.
/// Adopting this locator inside <c>PrefillDaemonServiceBase</c> to dedupe is an explicit follow-up.
///
/// Expected-cache-IP priority (contract amendments v1.1/v1.2): <c>Prefill__LancacheIp</c> config
/// ("config") -&gt; lancache-dns <c>.env</c>/Docker-inspect <c>LANCACHE_IP</c> via
/// <see cref="ILancacheEnvironmentSource"/> ("dockerInspect"/"envFile") -&gt; heartbeat-verified
/// Docker container auto-detection ("detected") -&gt; none determined ("none").
/// </summary>
public sealed class LancacheServerLocator : ILancacheServerLocator
{
    // Same config as PrefillDaemonServiceBase's static _heartbeatProbeClient - short timeouts,
    // shared/static to avoid per-call socket churn during Docker-detect candidate probing.
    private static readonly HttpClient _heartbeatProbeClient = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        // The probe must verify the address it was pointed at, never wherever that address
        // chooses to redirect it (the test-domain flow probes DNS-derived IPs).
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(2),
    };

    private readonly ILogger<LancacheServerLocator> _logger;
    private readonly IOptionsMonitor<PrefillNetworkOptions> _networkOptions;
    private readonly ILancacheEnvironmentSource _environmentSource;
    private readonly DockerClient? _dockerClient;

    public LancacheServerLocator(
        ILogger<LancacheServerLocator> logger,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions,
        ILancacheEnvironmentSource environmentSource)
    {
        _logger = logger;
        _networkOptions = networkOptions;
        _environmentSource = environmentSource;

        try
        {
            if (!OperatingSystemDetector.IsWindows && File.Exists("/var/run/docker.sock"))
            {
                _dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
                _logger.LogDebug("Status Check: Docker client initialized for lancache server auto-detection");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: Docker client not available - auto-detection will be unavailable");
        }
    }

    public async Task<LancacheServerLocation> LocateAsync(CancellationToken cancellationToken)
    {
        var configured = _networkOptions.CurrentValue.LancacheIp;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var resolved = await ResolveIpLiteralOrHostnameAsync(configured, cancellationToken);
            // Configured but unresolvable is still "config" sourced - explicit "no cache IP known"
            // state (empty CacheIps), never a guessed fallback to a lower-priority tier.
            return new LancacheServerLocation { CacheIps = resolved, Source = "config" };
        }

        var envResult = await _environmentSource.GetValueAsync("LANCACHE_IP", cancellationToken);
        if (!string.IsNullOrWhiteSpace(envResult.Value))
        {
            // lancache-dns accepts a whitespace-separated list of IPs in LANCACHE_IP for
            // round-robin answers (contract amendment v1.3) - every entry is an expected cache IP.
            var resolved = new List<string>();
            foreach (var entry in SplitAddressList(envResult.Value))
            {
                foreach (var ip in await ResolveIpLiteralOrHostnameAsync(entry, cancellationToken))
                {
                    if (!resolved.Contains(ip, StringComparer.OrdinalIgnoreCase))
                    {
                        resolved.Add(ip);
                    }
                }
            }
            var envSource = envResult.Source == EnvValueSource.DockerInspect ? "dockerInspect" : "envFile";
            return new LancacheServerLocation { CacheIps = resolved, Source = envSource };
        }

        var detected = await DetectAsync(cancellationToken);
        return detected != null
            ? new LancacheServerLocation { CacheIps = new List<string> { detected }, Source = "detected" }
            : new LancacheServerLocation { CacheIps = new List<string>(), Source = "none" };
    }

    /// <summary>Splits a lancache-dns style address list: whitespace-separated, comma/semicolon
    /// tolerant (e.g. <c>"1.2.3.4 1.2.3.5"</c> or <c>"1.2.3.4, 1.2.3.5"</c>).</summary>
    internal static string[] SplitAddressList(string value) =>
        value.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Resolves an IP literal directly, or a hostname via the system resolver (IPv4 only).
    /// Shared by the config-override and env-sourced (LANCACHE_IP) priority tiers, which both
    /// accept either form.</summary>
    private async Task<List<string>> ResolveIpLiteralOrHostnameAsync(string ipOrHostname, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(ipOrHostname, out _))
        {
            return new List<string> { ipOrHostname };
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(ipOrHostname, cancellationToken);
            var ipv4 = addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToList();

            if (ipv4.Count == 0)
            {
                _logger.LogWarning("Status Check: '{Value}' resolved but returned no IPv4 addresses", ipOrHostname);
            }

            return ipv4;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status Check: '{Value}' could not be resolved to an IP address", ipOrHostname);
            return new List<string>();
        }
    }

    public async Task<string?> DetectDnsServerIpAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null)
        {
            return null;
        }

        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = false }, cancellationToken);

            var dnsContainer = containers.FirstOrDefault(c => DockerContainerMatching.IsDnsContainer(c.Names ?? new List<string>()));

            if (dnsContainer == null)
            {
                return null;
            }

            var inspect = await _dockerClient.Containers.InspectContainerAsync(dnsContainer.ID, cancellationToken);
            if (inspect.HostConfig?.NetworkMode == "host")
            {
                _logger.LogInformation("Status Check: lancache-dns container uses host networking; treating as system resolver");
                return null;
            }

            var networkWithIp = inspect.NetworkSettings?.Networks?.Values
                .FirstOrDefault(n => !string.IsNullOrEmpty(n.IPAddress));

            if (networkWithIp != null)
            {
                _logger.LogInformation("Status Check: auto-detected lancache-dns IP {DnsIp} from container {ContainerName}",
                    networkWithIp.IPAddress, dnsContainer.Names.FirstOrDefault());
            }

            return networkWithIp?.IPAddress;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: failed to auto-detect lancache-dns container IP");
            return null;
        }
    }

    public async Task<HeartbeatResult> ProbeHeartbeatAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{ip}/lancache-heartbeat");
            using var response = await _heartbeatProbeClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            string? servedBy = null;
            if (response.Headers.TryGetValues("X-LanCache-Processed-By", out var headerValues) ||
                (response.Content?.Headers.TryGetValues("X-LanCache-Processed-By", out headerValues) ?? false))
            {
                servedBy = headerValues?.FirstOrDefault();
            }

            if (servedBy != null)
            {
                return new HeartbeatResult { Reachable = true, ServedBy = servedBy, CacheIp = ip, Error = null };
            }

            return new HeartbeatResult
            {
                Reachable = false,
                ServedBy = null,
                CacheIp = ip,
                Error = $"HTTP {(int)response.StatusCode} but no X-LanCache-Processed-By header (not a lancache server)"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new HeartbeatResult { Reachable = false, ServedBy = null, CacheIp = ip, Error = "cancelled" };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Connection refused / timeout / no HTTP listener - not the lancache cache server.
            return new HeartbeatResult
            {
                Reachable = false,
                ServedBy = null,
                CacheIp = ip,
                Error = $"probe failed ({ex.GetType().Name}: {ex.Message})"
            };
        }
    }

    private async Task<string?> DetectAsync(CancellationToken cancellationToken)
    {
        if (_dockerClient == null)
        {
            return null;
        }

        try
        {
            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = false }, cancellationToken);

            var candidates = new List<(string Ip, string Source)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string? ip, string source)
            {
                if (!string.IsNullOrWhiteSpace(ip) && IsPrivateIp(ip) && seen.Add(ip))
                {
                    candidates.Add((ip, source));
                }
            }

            var configuredDnsIp = _networkOptions.CurrentValue.LancacheDnsIp;
            if (!string.IsNullOrWhiteSpace(configuredDnsIp) &&
                !string.Equals(configuredDnsIp, "auto", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(configuredDnsIp, "Prefill__LancacheDnsIp");
            }

            var fallbackCandidates = new List<(string Ip, string Source)>();

            foreach (var c in containers)
            {
                var image = c.Image ?? string.Empty;
                var names = c.Names ?? new List<string>();

                if (DockerContainerMatching.IsManagerContainer(names, image))
                {
                    continue;
                }

                bool isDns = names.Any(n => n.Contains("dns", StringComparison.OrdinalIgnoreCase));

                ContainerInspectResponse inspect;
                try
                {
                    inspect = await _dockerClient.Containers.InspectContainerAsync(c.ID, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Status Check: failed to inspect container {Name}; skipping", names.FirstOrDefault());
                    continue;
                }

                if (inspect.HostConfig?.NetworkMode == "host")
                {
                    continue;
                }

                var nets = inspect.NetworkSettings?.Networks;
                if (nets == null)
                {
                    continue;
                }

                bool isLancacheContainer = DockerContainerMatching.IsLancacheCacheContainer(image, names);

                foreach (var ip in nets.Where(n => !string.IsNullOrEmpty(n.Value.IPAddress)).Select(n => n.Value.IPAddress!).Distinct())
                {
                    if (isDns)
                    {
                        continue; // DNS server IP is not the HTTP cache candidate here.
                    }

                    if (isLancacheContainer)
                    {
                        AddCandidate(ip, $"container {names.FirstOrDefault()} (image {image})");
                    }
                    else if (IsPrivateIp(ip) && !seen.Contains(ip))
                    {
                        fallbackCandidates.Add((ip, $"network peer {names.FirstOrDefault()} (image {image})"));
                    }
                }
            }

            foreach (var fc in fallbackCandidates)
            {
                AddCandidate(fc.Ip, fc.Source);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            foreach (var (ip, source) in candidates)
            {
                var probe = await ProbeHeartbeatAsync(ip, cancellationToken);
                if (probe.Reachable)
                {
                    _logger.LogInformation("Status Check: auto-detected lancache server {Ip} from {Source}, heartbeat verified", ip, source);
                    return ip;
                }
            }

            _logger.LogInformation("Status Check: none of the {Count} candidate IP(s) passed heartbeat verification", candidates.Count);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: failed to auto-detect lancache HTTP server IP");
            return null;
        }
    }

    /// <summary>Same private-range classification as <c>PrefillDaemonServiceBase.IsPrivateIp</c>
    /// (a small, self-contained helper - not worth extracting given the risk-containment decision
    /// not to touch that file).</summary>
    internal static bool IsPrivateIp(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            var first = bytes[0];
            var second = bytes[1];

            if (first == 10) return true;
            if (first == 172 && second >= 16 && second <= 31) return true;
            if (first == 192 && second == 168) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}
