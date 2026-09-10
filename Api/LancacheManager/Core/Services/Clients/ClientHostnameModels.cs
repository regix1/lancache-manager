using DnsClient;

namespace LancacheManager.Core.Services;

/// <summary>
/// One DNS server in the trusted reverse-lookup chain.
/// </summary>
internal sealed record HostnameResolver(LookupClient Client, string Address);

/// <summary>
/// A built resolver chain, when it was built, and the client subnets it was built for. The subnets
/// belong to it because the router addresses in the chain are derived from them, so a chain is only
/// reusable for a client whose subnet it already covers.
/// </summary>
internal sealed record CachedResolver(
    IReadOnlyList<HostnameResolver> Resolvers,
    DateTime CreatedAtUtc,
    IReadOnlySet<string> SubnetPrefixes);

/// <summary>
/// What one reverse lookup established: the name the network published, if any, and whether the
/// question was answered at all. "This machine has no reverse record" is a settled fact about the
/// network and stands for the full name window; "the query never got through" says nothing about
/// the address and is forgotten quickly so the next refresh asks again.
/// </summary>
internal readonly record struct HostnameLookupResult(string? Hostname, bool Answered);
