using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Runs the Status Check DNS sweep: resolves every cache-domains entry against the configured/
/// detected DNS resolver, compares against the lancache server's IP(s), and heartbeat-verifies the
/// cache itself. Also backs the ad hoc "test a domain" flow.
/// </summary>
public interface IStatusCheckService
{
    bool IsRunning { get; }
    Guid? CurrentOperationId { get; }

    /// <summary>The most recently completed sweep result, loaded from persisted state on first
    /// access. Null when no sweep has ever completed.</summary>
    StatusCheckResult? GetLastResult();

    /// <summary>
    /// Starts a sweep in the background and returns its operation ID immediately. Returns
    /// <c>null</c> without starting anything when a sweep is already running (caller returns 409).
    /// The sweep's lifetime is owned by the service: it is cancelled only through the operation
    /// tracker, never by the HTTP request that started it disconnecting after the 202.
    /// </summary>
    Guid? StartSweep();

    /// <summary>Resolves and verdicts a single ad hoc domain (dropdown selection or free text),
    /// heartbeat-probing its resolved IP when it resolves.</summary>
    Task<(DomainCheckResult Result, HeartbeatResult? Heartbeat)> TestDomainAsync(string domain, CancellationToken cancellationToken);

    /// <summary>The persisted DNS resolver mode ("auto" | "bridge" | "host"); an unknown or absent
    /// stored value is normalized to "auto" on read so a corrupt value never breaks the sweep.</summary>
    string GetResolverMode();

    /// <summary>Persists the DNS resolver mode. Throws <see cref="ArgumentException"/> for a value
    /// outside the allowed set (callers validate up-front to return 400 before reaching this).</summary>
    void SetResolverMode(string mode);
}
