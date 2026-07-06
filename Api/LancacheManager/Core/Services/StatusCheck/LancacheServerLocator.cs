using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DnsClient;
using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// The single shared detector for the lancache HTTP cache server IP and the lancache-dns container
/// IP, plus the <c>/lancache-heartbeat</c> probe. Used by BOTH the Status Check feature and
/// <c>PrefillDaemonServiceBase</c> (which injects the result as the daemon's <c>LANCACHE_IP</c> and
/// <c>HostConfig.DNS</c>) - prefill's former private copies of this logic were removed in favor of
/// this class. Caller scope is expressed through <see cref="LocateAsync(bool, CancellationToken)"/>'s
/// host-side-candidate flag and the internal <see cref="CacheCandidateProfile"/>, so Status Check and
/// prefill share one code path without either weakening the other's contract.
///
/// Expected-cache-IP priority (contract amendments v1.1/v1.2): <c>Prefill__LancacheIp</c> config
/// ("config") -&gt; lancache-dns <c>.env</c>/Docker-inspect <c>LANCACHE_IP</c> via
/// <see cref="ILancacheEnvironmentSource"/> ("dockerInspect"/"envFile") -&gt; heartbeat-verified
/// Docker container auto-detection ("detected") -&gt; none determined ("none").
/// </summary>
public sealed class LancacheServerLocator : ILancacheServerLocator
{
    // Short timeouts, shared/static to avoid per-call socket churn during Docker-detect candidate
    // probing. AllowAutoRedirect=false so the probe verifies the address it was pointed at.
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

    // The canonical universal lancache test domain: lancache-dns poisons it to the LAN cache IP,
    // upstream/public DNS returns the real Steam CDN. Same probe host the reference resolver uses.
    private const string LancacheTestDomain = "lancache.steamcontent.com";

    // Once-per-sweep detection tries candidates sequentially; cap the count so a run of dead
    // candidates (each ~2s) can't stall the sweep.
    private const int MaxDnsCandidateProbes = 6;

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

    public Task<LancacheServerLocation> LocateAsync(CancellationToken cancellationToken)
        => LocateAsync(includeHostSideCandidates: false, cancellationToken);

    public async Task<LancacheServerLocation> LocateAsync(bool includeHostSideCandidates, CancellationToken cancellationToken)
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

        var profile = includeHostSideCandidates
            ? CacheCandidateProfile.Prefill
            : CacheCandidateProfile.StatusCheck;
        var detected = await DetectAsync(profile, cancellationToken);
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

    public async Task<string?> DetectDnsServerIpAsync(string mode, IReadOnlyList<string>? knownCacheIps, CancellationToken cancellationToken)
    {
        // (a) Bridge-mode: a non-host-networked lancache-dns container has a reachable bridge IP.
        //     Needs Docker; skipped (null) when the socket is absent or the dns container is host-networked.
        //     "host" mode skips this path entirely (host-only candidates by request).
        string? dnsBridgeIp = null;
        if (mode != StatusCheckResolverModes.Host && _dockerClient != null)
        {
            dnsBridgeIp = await TryDetectDnsBridgeIpAsync(cancellationToken);
        }

        // (b) Fall through to host-DNS candidate probing (scoped by mode). The bridge gateway / known
        //     cache IP / host.docker.internal / loopback candidates need no Docker, so they run even when
        //     the socket is absent, and they cover the host-networked lancache-dns/monolithic case where
        //     (a) is null. In "bridge" mode BuildDnsServerCandidates keeps only the bridge IP.
        return await ProbeHostDnsCandidatesAsync(mode, dnsBridgeIp, knownCacheIps, cancellationToken);
    }

    public async Task<string?> DetectDnsContainerBridgeIpAsync(CancellationToken cancellationToken)
    {
        // Explicit Prefill__LancacheDnsIp wins. This short-circuit is what makes the method a drop-in
        // replacement for the prefill copy's GetLancacheDnsIpAsync used on HostConfig.DNS - the
        // internal TryDetectDnsBridgeIpAsync (Status Check reporting) deliberately omits it.
        var configuredIp = _networkOptions.CurrentValue.LancacheDnsIp;
        if (!string.IsNullOrWhiteSpace(configuredIp) &&
            !string.Equals(configuredIp, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return configuredIp;
        }

        if (_dockerClient == null)
        {
            return null;
        }

        // Bridge IP of the running lancache-dns container, or null when it is host-networked (H2: the
        // caller reads null as the "switch to host mode" signal). NEVER a gateway/host-side fallback
        // here - HostConfig.DNS must point only at a real bridge dns container.
        return await TryDetectDnsBridgeIpAsync(cancellationToken);
    }

    /// <summary>Docker-inspect path for a bridge-mode lancache-dns container: returns its bridge IP,
    /// or <c>null</c> when there is no dns container, it uses host networking (no bridge IP), or the
    /// inspect fails. Host-networked dns is intentionally handled by the candidate probe instead.</summary>
    private async Task<string?> TryDetectDnsBridgeIpAsync(CancellationToken cancellationToken)
    {
        try
        {
            var containers = await _dockerClient!.Containers.ListContainersAsync(
                new ContainersListParameters { All = false }, cancellationToken);

            var dnsContainer = containers.FirstOrDefault(c => DockerContainerMatching.IsDnsContainer(c.Names ?? new List<string>()));
            if (dnsContainer == null)
            {
                return null;
            }

            var inspect = await _dockerClient.Containers.InspectContainerAsync(dnsContainer.ID, cancellationToken);
            var isHostNetworked = inspect.HostConfig?.NetworkMode == "host";
            var networkIps = inspect.NetworkSettings?.Networks?.Values.Select(n => n.IPAddress)
                             ?? Enumerable.Empty<string?>();

            var bridgeIp = SelectDnsBridgeIp(isHostNetworked, networkIps);
            if (isHostNetworked)
            {
                _logger.LogDebug("Status Check: lancache-dns uses host networking; probing host DNS candidates instead of a bridge IP");
            }
            else if (bridgeIp != null)
            {
                _logger.LogDebug("Status Check: found lancache-dns bridge IP {DnsIp} from container {ContainerName}",
                    bridgeIp, dnsContainer.Names?.FirstOrDefault());
            }

            return bridgeIp;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: failed to inspect lancache-dns container for its bridge IP");
            return null;
        }
    }

    /// <summary>Pure decision for a lancache-dns container's bridge IP: <c>null</c> when the container
    /// is host-networked (no bridge IP - the caller's "switch to host mode" signal, H2), otherwise the
    /// first non-empty network IP. Static/pure so the host-networked null semantics are unit-testable
    /// without a live Docker daemon.</summary>
    internal static string? SelectDnsBridgeIp(bool isHostNetworked, IEnumerable<string?> networkIpAddresses)
    {
        if (isHostNetworked)
        {
            return null;
        }

        return networkIpAddresses?.FirstOrDefault(ip => !string.IsNullOrEmpty(ip));
    }

    /// <summary>Builds an ordered DNS-server candidate list and returns the first that verifies as a
    /// real lancache DNS (see <see cref="VerifyDnsCandidateAsync"/>). resolverSource stays "detected".</summary>
    private async Task<string?> ProbeHostDnsCandidatesAsync(
        string mode, string? dnsBridgeIp, IReadOnlyList<string>? knownCacheIps, CancellationToken cancellationToken)
    {
        var gatewayIp = GetDefaultGatewayIp();
        var hostDockerInternalIps = await ResolveHostDockerInternalIpsAsync(cancellationToken);

        var candidates = BuildDnsServerCandidates(mode, dnsBridgeIp, gatewayIp, knownCacheIps, hostDockerInternalIps);
        if (candidates.Count == 0)
        {
            return null;
        }

        foreach (var candidate in candidates.Take(MaxDnsCandidateProbes))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (await VerifyDnsCandidateAsync(candidate, cancellationToken))
            {
                _logger.LogInformation(
                    "Status Check: auto-detected lancache DNS server {DnsIp} (verified via {TestDomain})",
                    candidate, LancacheTestDomain);
                return candidate;
            }
        }

        _logger.LogInformation(
            "Status Check: none of the {Count} candidate DNS server IP(s) verified as a lancache DNS; using system resolver",
            candidates.Count);
        return null;
    }

    /// <summary>Ordered, deduped, SSRF-gated DNS-server candidate list (first that verifies wins), scoped
    /// by <paramref name="mode"/> ("auto" | "bridge" | "host"):
    /// (1) bridge-mode lancache-dns IP - included by "auto"/"bridge", excluded by "host";
    /// (2) Docker bridge default gateway - the host-networked case;
    /// (3) known cache IP(s) - a monolithic image co-locates DNS+cache;
    /// (4) <c>host.docker.internal</c>;
    /// (5) loopback LAST - only meaningful if the manager itself is host-networked.
    /// Groups (2)-(5) are the host-side set, included by "auto"/"host" and excluded by "bridge".
    /// Every entry is gated through <see cref="IsProbeableCandidateIp"/> so a public IP is never even
    /// probed. Pure/static for unit-testing the ordering, mode scoping, and gating without the network.</summary>
    internal static List<string> BuildDnsServerCandidates(
        string mode,
        string? dnsBridgeIp,
        string? gatewayIp,
        IReadOnlyList<string>? knownCacheIps,
        IReadOnlyList<string>? hostDockerInternalIps)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? ip)
        {
            if (!string.IsNullOrWhiteSpace(ip) && IsProbeableCandidateIp(ip!) && seen.Add(ip!))
            {
                ordered.Add(ip!);
            }
        }

        // Bridge-mode lancache-dns candidate: part of "auto" and "bridge", omitted in "host".
        if (mode != StatusCheckResolverModes.Host)
        {
            Add(dnsBridgeIp); // 1) bridge-mode lancache-dns
        }

        // Host-side candidate group: part of "auto" and "host", omitted in "bridge".
        if (mode != StatusCheckResolverModes.Bridge)
        {
            Add(gatewayIp); // 2) Docker bridge default gateway (host-networked lancache-dns/monolithic)

            if (knownCacheIps != null)
            {
                foreach (var ip in knownCacheIps)
                {
                    Add(ip); // 3) monolithic co-locates DNS + cache on one host IP
                }
            }

            if (hostDockerInternalIps != null)
            {
                foreach (var ip in hostDockerInternalIps)
                {
                    Add(ip); // 4) host.docker.internal (Docker Desktop; harmless elsewhere)
                }
            }

            Add("127.0.0.1"); // 5) LAST - only reaches a DNS server if the manager itself is host-networked
        }

        return ordered;
    }

    /// <summary>Verifies a candidate really is a lancache DNS: point a 2s no-retry <see cref="LookupClient"/>
    /// at it, query the test domain, accept iff an A-record answer is a private IP that also passes a
    /// <c>/lancache-heartbeat</c> probe (proves a real lancache sits at the poisoned answer, not merely a
    /// private-answering resolver). Never constructs a resolver against a non-private/non-loopback IP (SSRF).</summary>
    private async Task<bool> VerifyDnsCandidateAsync(string candidateIp, CancellationToken cancellationToken)
    {
        // SSRF bound: only ever build a resolver against a private/loopback candidate.
        if (!IsProbeableCandidateIp(candidateIp) || !IPAddress.TryParse(candidateIp, out var candidateAddress))
        {
            return false;
        }

        try
        {
            var options = new LookupClientOptions(candidateAddress)
            {
                Timeout = TimeSpan.FromSeconds(2),
                Retries = 0,
                UseCache = false,
            };
            var client = new LookupClient(options);
            var response = await client.QueryAsync(LancacheTestDomain, QueryType.A, cancellationToken: cancellationToken);

            // Accept only a PRIVATE answer - a public answer is exactly the cache-bypass this tool detects.
            var privateAnswer = response.Answers.ARecords()
                .Select(r => r.Address.ToString())
                .FirstOrDefault(IsPrivateIp);

            if (privateAnswer == null)
            {
                return false;
            }

            // Confidence booster: prove an actual lancache is serving that IP.
            var heartbeat = await ProbeHeartbeatAsync(privateAnswer, cancellationToken);
            if (heartbeat.Reachable)
            {
                _logger.LogDebug(
                    "Status Check: DNS candidate {Candidate} poisons {TestDomain} -> {Answer} (heartbeat verified)",
                    candidateIp, LancacheTestDomain, privateAnswer);
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Status Check: DNS candidate {Candidate} did not verify", candidateIp);
            return false;
        }
    }

    /// <summary>Resolves <c>host.docker.internal</c> to its private IPv4 address(es), or an empty list
    /// when it doesn't resolve (stock compose) or resolves to a public IP.</summary>
    private static async Task<List<string>> ResolveHostDockerInternalIpsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("host.docker.internal", cancellationToken);
            return addresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Where(IsPrivateIp)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>The Docker bridge default gateway IP (e.g. <c>172.17.0.1</c>) - the address a
    /// bridge-networked container uses to reach a service listening on the host, such as a
    /// host-networked lancache-dns on <c>:53</c>. Mirrors the reference resolver's
    /// <c>GetDefaultGateway()</c> (first IPv4 gateway of an Up, non-loopback interface).</summary>
    private static string? GetDefaultGatewayIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties()?.GatewayAddresses ?? Enumerable.Empty<GatewayIPAddressInformation>())
                .Select(g => g?.Address)
                .FirstOrDefault(a => a != null && a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch
        {
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

    /// <summary>Caller-scoping profile for the cache-server candidate builder. There is deliberately
    /// no IncludeLoopback flag: loopback is meaningless as an injected <c>LANCACHE_IP</c> and useless
    /// inside a bridge container, so it is NEVER a cache candidate (H3). Loopback stays valid only in
    /// <see cref="BuildDnsServerCandidates"/> (the DNS resolver path).</summary>
    internal sealed record CacheCandidateProfile(
        bool InspectContainers,
        bool IncludeDnsContainerIp,
        bool IncludeGateway,
        bool IncludeHostDockerInternal)
    {
        /// <summary>Status Check: Docker container bridge IPs + the monolithic dns-IP only, no host-side
        /// candidates - keeps the resolverSource/wire contract frozen. A monolithic dns-IP that also
        /// serves cache is still a legit heartbeat-verified "detected" cache IP (H1).</summary>
        internal static readonly CacheCandidateProfile StatusCheck =
            new(InspectContainers: true, IncludeDnsContainerIp: true, IncludeGateway: false, IncludeHostDockerInternal: false);

        /// <summary>Prefill: additionally heartbeat-probes the Docker bridge default gateway and
        /// <c>host.docker.internal</c>, so a HOST-NETWORKED lancache box (no bridge-container IP)
        /// auto-detects and is injected as <c>LANCACHE_IP</c>.</summary>
        internal static readonly CacheCandidateProfile Prefill =
            new(InspectContainers: true, IncludeDnsContainerIp: true, IncludeGateway: true, IncludeHostDockerInternal: true);
    }

    private async Task<string?> DetectAsync(CacheCandidateProfile profile, CancellationToken cancellationToken)
    {
        // Explicit configured DNS IP (a monolithic image co-locates DNS+cache on one host IP) - an
        // H1 cache candidate that needs no Docker.
        string? configuredDnsIp = null;
        var cfgDns = _networkOptions.CurrentValue.LancacheDnsIp;
        if (!string.IsNullOrWhiteSpace(cfgDns) &&
            !string.Equals(cfgDns, "auto", StringComparison.OrdinalIgnoreCase))
        {
            configuredDnsIp = cfgDns;
        }

        string? dnsBridgeIp = null;
        var containerCacheCandidates = new List<(string Ip, string Source)>();
        var fallbackPeerCandidates = new List<(string Ip, string Source)>();

        if (profile.InspectContainers && _dockerClient != null)
        {
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(
                    new ContainersListParameters { All = false }, cancellationToken);

                foreach (var c in containers)
                {
                    var image = c.Image ?? string.Empty;
                    var names = c.Names ?? new List<string>();

                    // The manager serves no CDN content; injecting its IP dead-ends the daemon's TACT
                    // requests. (It also fails the heartbeat, but skip it up front regardless.)
                    if (DockerContainerMatching.IsManagerContainer(names, image))
                    {
                        continue;
                    }

                    bool isDns = DockerContainerMatching.IsDnsContainer(names);

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
                            // Monolithic co-locates DNS+cache: keep the dns bridge IP as an H1 cache
                            // candidate (heartbeat-gated in the builder) instead of discarding it.
                            dnsBridgeIp ??= ip;
                            continue;
                        }

                        if (isLancacheContainer)
                        {
                            containerCacheCandidates.Add((ip, $"container {names.FirstOrDefault()} (image {image})"));
                        }
                        else
                        {
                            fallbackPeerCandidates.Add((ip, $"network peer {names.FirstOrDefault()} (image {image})"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Status Check: failed to enumerate containers for lancache cache auto-detection");
            }
        }

        // Host-side candidates (prefill profile only): a host-networked cache has no bridge container,
        // so the Docker bridge default gateway / host.docker.internal are the only routes to it.
        var gatewayIp = profile.IncludeGateway ? GetDefaultGatewayIp() : null;
        var hostDockerInternalIps = profile.IncludeHostDockerInternal
            ? await ResolveHostDockerInternalIpsAsync(cancellationToken)
            : new List<string>();

        var ordered = BuildCacheCandidates(
            profile, configuredDnsIp, dnsBridgeIp, containerCacheCandidates, fallbackPeerCandidates, gatewayIp, hostDockerInternalIps);

        if (ordered.Count == 0)
        {
            return null;
        }

        foreach (var (ip, source) in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var probe = await ProbeHeartbeatAsync(ip, cancellationToken);
            if (probe.Reachable)
            {
                _logger.LogInformation("Status Check: auto-detected lancache server {Ip} from {Source}, heartbeat verified", ip, source);
                return ip;
            }
        }

        _logger.LogInformation("Status Check: none of the {Count} candidate IP(s) passed heartbeat verification", ordered.Count);
        return null;
    }

    /// <summary>Ordered, deduped, private-only cache-server candidate list (first that heartbeat-verifies
    /// wins), scoped by <paramref name="profile"/>:
    /// (1) explicit <c>Prefill__LancacheDnsIp</c> and (2) detected lancache-dns bridge IP - a monolithic
    /// image co-locates DNS+cache (both gated by <see cref="CacheCandidateProfile.IncludeDnsContainerIp"/>,
    /// H1);
    /// (3) named lancache/monolithic cache containers;
    /// (4) Docker bridge default gateway - the host-networked cache case (prefill profile only);
    /// (5) <c>host.docker.internal</c> (prefill profile only);
    /// (6) unnamed bridge peers - last resort.
    /// Every entry is gated through <see cref="IsPrivateIp"/>, which also EXCLUDES loopback
    /// (127.0.0.0/8 is not RFC1918), so a loopback IP can never be injected as <c>LANCACHE_IP</c> (H3).
    /// Pure/static so the ordering, profile scoping, dedupe, and loopback exclusion are unit-testable
    /// without the network.</summary>
    internal static List<(string Ip, string Source)> BuildCacheCandidates(
        CacheCandidateProfile profile,
        string? configuredDnsIp,
        string? dnsBridgeIp,
        IReadOnlyList<(string Ip, string Source)> containerCacheCandidates,
        IReadOnlyList<(string Ip, string Source)> fallbackPeerCandidates,
        string? gatewayIp,
        IReadOnlyList<string>? hostDockerInternalIps)
    {
        var ordered = new List<(string Ip, string Source)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? ip, string source)
        {
            if (!string.IsNullOrWhiteSpace(ip) && IsPrivateIp(ip!) && seen.Add(ip!))
            {
                ordered.Add((ip!, source));
            }
        }

        if (profile.IncludeDnsContainerIp)
        {
            Add(configuredDnsIp, "Prefill__LancacheDnsIp"); // 1) explicit monolithic DNS+cache IP
            Add(dnsBridgeIp, "lancache-dns container");     // 2) detected monolithic DNS+cache IP
        }

        foreach (var (ip, source) in containerCacheCandidates)
        {
            Add(ip, source); // 3) named lancache/monolithic cache containers
        }

        if (profile.IncludeGateway)
        {
            Add(gatewayIp, "bridge gateway"); // 4) host-networked cache reachable via the bridge gateway
        }

        if (profile.IncludeHostDockerInternal && hostDockerInternalIps != null)
        {
            foreach (var ip in hostDockerInternalIps)
            {
                Add(ip, "host.docker.internal"); // 5) Docker Desktop host cache
            }
        }

        foreach (var (ip, source) in fallbackPeerCandidates)
        {
            Add(ip, source); // 6) unnamed bridge peers - last resort
        }

        return ordered;
    }

    /// <summary>An IP the DNS-candidate probe may safely point a resolver at: RFC1918 private, OR
    /// loopback (<c>127.0.0.0/8</c> - reaches only the manager itself, so SSRF-safe and meaningful when
    /// the manager is host-networked). This is the SSRF gate for CANDIDATES; DNS ANSWERS are still
    /// accepted only when <see cref="IsPrivateIp"/> (a loopback answer would be nonsense).</summary>
    internal static bool IsProbeableCandidateIp(string ip)
    {
        if (string.IsNullOrEmpty(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        return IsPrivateIp(ip) || IPAddress.IsLoopback(address);
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
