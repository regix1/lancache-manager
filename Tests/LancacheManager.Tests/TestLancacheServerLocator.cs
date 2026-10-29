using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// No-op <see cref="ILancacheServerLocator"/> for the daemon-service unit tests, which construct a
/// real <c>PrefillDaemonServiceBase</c> subclass but never drive the Docker container-creation inject
/// path. Locate returns the explicit "no cache IP known" state (empty CacheIps, Source "none") and the
/// DNS/heartbeat probes return the not-found result, so no test accidentally depends on live detection.
/// </summary>
internal sealed class TestLancacheServerLocator : ILancacheServerLocator
{
    private readonly IReadOnlyList<string> _lanResolverIps;

    /// <summary>Counts the Docker-backed lookups, so a test can prove one was never made.</summary>
    private readonly Action? _onDetect;

    /// <summary>What both Locate overloads answer; a test passes one to model a configured address that did not resolve.</summary>
    private readonly LancacheServerLocation _location;

    public TestLancacheServerLocator(IReadOnlyList<string>? lanResolverIps = null, Action? onDetect = null,
        LancacheServerLocation? location = null)
    {
        _lanResolverIps = lanResolverIps ?? Array.Empty<string>();
        _onDetect = onDetect;
        _location = location ?? new LancacheServerLocation();
    }

    public Task<LancacheServerLocation> LocateAsync(CancellationToken cancellationToken)
        => Task.FromResult(_location);

    public Task<LancacheServerLocation> LocateAsync(bool includeHostSideCandidates, CancellationToken cancellationToken)
        => Task.FromResult(_location);

    public Task<string?> DetectDnsServerIpAsync(string mode, IReadOnlyList<string>? knownCacheIps, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<string?> DetectDnsContainerBridgeIpAsync(CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> DetectLanResolverIpsAsync(CancellationToken cancellationToken)
    {
        _onDetect?.Invoke();
        return Task.FromResult(_lanResolverIps);
    }

    public Task<IReadOnlyList<string>> DetectHostDockerInternalIpsAsync(CancellationToken cancellationToken)
    {
        _onDetect?.Invoke();
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<HeartbeatResult> ProbeHeartbeatAsync(string ip, CancellationToken cancellationToken)
        => Task.FromResult(new HeartbeatResult { Reachable = false, CacheIp = ip, Error = "test locator" });
}
