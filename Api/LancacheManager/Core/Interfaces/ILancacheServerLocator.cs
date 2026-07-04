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
/// <c>/lancache-heartbeat</c> endpoint. Re-implements (does not modify) the equivalent logic in
/// <c>PrefillDaemonServiceBase</c> - kept separate per the swarm's risk-containment decision not to
/// touch that 3700-line prefill-critical file.
/// </summary>
public interface ILancacheServerLocator
{
    /// <summary>Resolves the expected cache server IP(s) via <c>Prefill__LancacheIp</c> (IP literal
    /// or DNS-resolved hostname) or, when unset, heartbeat-verified Docker container auto-detection.</summary>
    Task<LancacheServerLocation> LocateAsync(CancellationToken cancellationToken);

    /// <summary>Detects the lancache-dns container's IP the same way prefill container creation
    /// does. Returns <c>null</c> when undetectable or when the container uses host networking
    /// (callers should fall back to the system resolver in that case).</summary>
    Task<string?> DetectDnsServerIpAsync(CancellationToken cancellationToken);

    /// <summary>Probes <c>GET http://{ip}/lancache-heartbeat</c> and reports whether the
    /// <c>X-LanCache-Processed-By</c> header was present (and its value).</summary>
    Task<HeartbeatResult> ProbeHeartbeatAsync(string ip, CancellationToken cancellationToken);
}
