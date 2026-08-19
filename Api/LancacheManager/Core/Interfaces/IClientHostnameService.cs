using LancacheManager.Models;

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
    /// Maps each address that has a reverse-DNS name to that name, without its trailing dot, and
    /// says why any address (or all of them) has none.
    /// Returns an empty map with reason <see cref="ClientHostnamesReason.None"/> and issues zero
    /// queries when the lookup is off. Addresses outside the private ranges are never resolved, and
    /// an address the network has no name for is simply absent from the map rather than an error.
    /// </summary>
    Task<ClientHostnameLookupOutcome> ResolveAsync(
        IReadOnlyCollection<string> clientIps,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every address the network publishes for one name, asked of the same resolver the reverse
    /// lookup uses, so a machine that has not downloaded anything yet can still be found by name.
    /// Unlike <see cref="ResolveAsync"/> this ignores the global toggle: that toggle exists because
    /// reverse names are looked up for every row on their own, while this runs only when someone
    /// types a name and asks for it.
    /// </summary>
    Task<ClientAddressLookupOutcome> ResolveAddressesAsync(
        string hostname,
        CancellationToken cancellationToken);
}
