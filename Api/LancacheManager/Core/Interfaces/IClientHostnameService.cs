namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Reverse-DNS names for client addresses, asked of the network's own DNS server and cached.
/// The lookup is a global opt-in and off by default: a network with no reverse zone is the normal
/// case, not a fault, and every surface falls back to the raw address when no name comes back.
/// </summary>
public interface IClientHostnameService
{
    /// <summary>Whether the global hostname lookup is turned on.</summary>
    bool IsEnabled();

    /// <summary>Persists the global hostname lookup toggle.</summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Maps each address that has a reverse-DNS name to that name, without its trailing dot.
    /// Returns an empty map and issues zero queries when the lookup is off. Addresses outside the
    /// private ranges are never resolved, and an address the network has no name for is simply
    /// absent from the result rather than an error.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IReadOnlyCollection<string> clientIps,
        CancellationToken cancellationToken);
}
