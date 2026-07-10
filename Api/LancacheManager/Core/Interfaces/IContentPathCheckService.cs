using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Interfaces;

/// <summary>Builds bounded empirical cache and public-edge observations from recent real paths.</summary>
public interface IContentPathCheckService
{
    Task<StatusCheckContentReport> CheckAsync(
        IReadOnlyList<CacheDomainService> services,
        CancellationToken cancellationToken);

    /// <summary>Ad hoc single-host public-edge HTTP/HTTPS probe for the test-a-domain flow.
    /// Reuses the sweep lane's DoH resolution, pinned one-byte probe, edge selection, and
    /// consensus rules against the host's root path. Fail-soft: resolution problems become a
    /// typed protocol reason, never an exception.</summary>
    Task<HostProtocolProbeResult> ProbeHostAsync(string host, CancellationToken cancellationToken);
}
