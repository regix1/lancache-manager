using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Interfaces;

/// <summary>Where the lancache HTTP cache server was found, and how.</summary>
public sealed class LancacheServerLocation
{
    /// <summary>The IPv4 address(es) that should be serving cache traffic. Empty when no cache IP
    /// could be determined - callers must treat this as an explicit "unknown" state, never invent
    /// a fallback value.</summary>
    public List<string> CacheIps { get; init; } = new();

    /// <summary>"configured" | "detected" | "none".</summary>
    public string Source { get; init; } = "none";
}

/// <summary>
/// Locates the lancache HTTP cache server's IP (config override -&gt; DNS resolve -&gt; Docker-detect
/// fallback, heartbeat-verified) and the lancache-dns container's IP, and probes the
/// <c>/lancache-heartbeat</c> endpoint. The single shared detector for both the Status Check feature
/// and <c>PrefillDaemonServiceBase</c> (which injects the result as the daemon's <c>LANCACHE_IP</c>
/// and <c>HostConfig.DNS</c>); prefill's former private copies of this logic were removed in favor of
/// this contract.
/// </summary>
public interface ILancacheServerLocator
{
    /// <summary>Resolves the expected cache server IP(s) via <c>Prefill__LancacheIp</c> (IP literal
    /// or DNS-resolved hostname) or, when unset, heartbeat-verified Docker container auto-detection.</summary>
    Task<LancacheServerLocation> LocateAsync(CancellationToken cancellationToken);

    /// <summary>Cache-server resolution that opts into host-side candidates (the Docker bridge default
    /// gateway and <c>host.docker.internal</c>), so a HOST-NETWORKED lancache box - which exposes no
    /// bridge-container IP - still auto-detects and can be injected as <c>LANCACHE_IP</c>. Every
    /// candidate stays heartbeat-verified (<c>X-LanCache-Processed-By</c>) and loopback is never a
    /// candidate. <paramref name="includeHostSideCandidates"/> <c>false</c> is byte-identical to
    /// <see cref="LocateAsync(CancellationToken)"/> (the frozen Status Check contract); prefill passes
    /// <c>true</c>.</summary>
    Task<LancacheServerLocation> LocateAsync(bool includeHostSideCandidates, CancellationToken cancellationToken);

    /// <summary>Detects the on-host lancache DNS server's IP without requiring the user to set
    /// <c>Prefill__LancacheDnsIp</c>. <paramref name="mode"/> ("auto" | "bridge" | "host") scopes the
    /// candidate set: "auto" tries the bridge-mode lancache-dns container IP first, then falls through
    /// to an ordered, heartbeat-verified host-DNS candidate probe (Docker bridge gateway, known cache
    /// IP(s), <c>host.docker.internal</c>, loopback) that also covers the host-networked
    /// lancache-dns/monolithic case (which has no bridge IP); "bridge" tries ONLY the container's
    /// bridge IP; "host" tries ONLY the host-side candidates and skips the Docker bridge-container path.
    /// <paramref name="knownCacheIps"/> are the located cache server IP(s) - a monolithic image
    /// co-locates DNS+cache on the same host, so a known cache IP is a strong DNS candidate. Returns
    /// <c>null</c> when nothing verifies as a real lancache DNS (callers fall back to the system resolver).</summary>
    Task<string?> DetectDnsServerIpAsync(string mode, IReadOnlyList<string>? knownCacheIps, CancellationToken cancellationToken);

    /// <summary>Detects the lancache-dns container's BRIDGE IP for a prefill container's
    /// <c>HostConfig.DNS</c>: explicit <c>Prefill__LancacheDnsIp</c> wins; otherwise the running
    /// lancache-dns container's bridge IP; <c>null</c> when there is no dns container OR it uses host
    /// networking (the caller reads that <c>null</c> as the "switch the prefill container to host
    /// mode" signal). Deliberately DISTINCT from <see cref="DetectDnsServerIpAsync"/> - the richer
    /// gateway/loopback resolver used for Status Check reporting, which must NEVER feed
    /// <c>HostConfig.DNS</c>.</summary>
    Task<string?> DetectDnsContainerBridgeIpAsync(CancellationToken cancellationToken);

    /// <summary>Probes <c>GET http://{ip}/lancache-heartbeat</c> and reports whether the
    /// <c>X-LanCache-Processed-By</c> header was present (and its value).</summary>
    Task<HeartbeatResult> ProbeHeartbeatAsync(string ip, CancellationToken cancellationToken);

    /// <summary>Asks the REAL upstream CDN behind <paramref name="domain"/> whether plain HTTP
    /// gets bounced to HTTPS: sends <c>GET http://{upstreamIp}/</c> with <c>Host: {domain}</c>
    /// (redirects never followed) and reports a 3xx answer whose Location is an absolute
    /// <c>https://</c> URL - the upgrade game clients would follow around the cache. The target
    /// must be a PUBLIC address the caller resolved via the upstream resolver; private/loopback
    /// targets return the undeterminable result, because probing the cache itself would write
    /// synthetic lines into access.log that this app's parser and every third-party log consumer
    /// would then have to special-case away. Callers only pass curated cache-domains entries,
    /// never arbitrary user input.</summary>
    Task<HttpsRedirectProbeResult> ProbeHttpsRedirectAsync(string upstreamIp, string domain, CancellationToken cancellationToken);
}
