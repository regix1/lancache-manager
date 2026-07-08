using LancacheManager.Core.Interfaces;
using LancacheManager.Models.Responses;

namespace LancacheManager.Tests;

/// <summary>
/// No-op <see cref="ILancacheServerLocator"/> for the daemon-service unit tests, which construct a
/// real <c>PrefillDaemonServiceBase</c> subclass but never drive the Docker container-creation inject
/// path. Locate returns the explicit "no cache IP known" state (empty CacheIps, Source "none") and the
/// DNS/heartbeat probes return the not-found result, so no test accidentally depends on live detection.
/// </summary>
internal sealed class TestLancacheServerLocator : ILancacheServerLocator
{
    public Task<LancacheServerLocation> LocateAsync(CancellationToken cancellationToken)
        => Task.FromResult(new LancacheServerLocation());

    public Task<LancacheServerLocation> LocateAsync(bool includeHostSideCandidates, CancellationToken cancellationToken)
        => Task.FromResult(new LancacheServerLocation());

    public Task<string?> DetectDnsServerIpAsync(string mode, IReadOnlyList<string>? knownCacheIps, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<string?> DetectDnsContainerBridgeIpAsync(CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<HeartbeatResult> ProbeHeartbeatAsync(string ip, CancellationToken cancellationToken)
        => Task.FromResult(new HeartbeatResult { Reachable = false, CacheIp = ip, Error = "test locator" });

    public Task<HttpsRedirectProbeResult> ProbeHttpsRedirectAsync(string upstreamIp, string domain, CancellationToken cancellationToken)
        => Task.FromResult(new HttpsRedirectProbeResult { Redirected = null, Location = null });
}
