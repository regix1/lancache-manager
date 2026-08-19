using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Why <c>Hostnames</c> on <see cref="ClientHostnamesResponse"/> is missing a name for some or all
/// of the addresses that were asked about, or why nothing was asked at all. <see cref="None"/> and
/// <see cref="NoClients"/> need no explanation on screen; the rest do.
/// </summary>
[JsonConverter(typeof(ClientHostnamesReasonJsonConverter))]
public enum ClientHostnamesReason
{
    /// <summary>Names were found, or there is nothing to explain.</summary>
    None,

    /// <summary>No private addresses were left to look up once public and filtered addresses were removed.</summary>
    NoClients,

    /// <summary>No lancache DNS server could be found to ask, so the lookup fell back to the system resolver.</summary>
    NoResolver,

    /// <summary>
    /// The DNS server answered, and no name came back for any of the addresses. Some lookups may not
    /// have settled; one that did is enough to say the server is reachable and simply has no name.
    /// </summary>
    NoRecords,

    /// <summary>The DNS server did not answer any of the queries in time.</summary>
    ResolverTimeout,

    /// <summary>
    /// Some addresses got a name and some did not. Names were found, so the lookup plainly works;
    /// saying nothing here would leave the addresses that stayed bare looking like an accident.
    /// </summary>
    SomeUnnamed,

    /// <summary>The batch budget elapsed with lookups still running; the remaining names arrive with the next refresh.</summary>
    StillLooking,

    /// <summary>
    /// There were more private addresses than one lookup will ask about, so the ones past the cap
    /// were never queried. Without this they would sit bare beside named rows with the outcome
    /// claiming everything asked for was answered.
    /// </summary>
    TooManyClients
}

internal sealed class ClientHostnamesReasonJsonConverter : JsonStringEnumConverter<ClientHostnamesReason>
{
    public ClientHostnamesReasonJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// What one reverse-DNS lookup call established: the names that were found, and why any address (or
/// all of them) has none.
/// </summary>
public sealed record ClientHostnameLookupOutcome(
    IReadOnlyDictionary<string, string> Hostnames,
    ClientHostnamesReason Reason);

/// <summary>
/// Response for GET api/clients/hostnames: whether the lookup is on, a name for each client
/// address the network published one for, and why any address (or all of them) has none. Names
/// carry no trailing dot, and an address with no name is simply absent from the map.
/// </summary>
public class ClientHostnamesResponse
{
    public bool Enabled { get; set; }
    public Dictionary<string, string> Hostnames { get; set; } = new();
    public ClientHostnamesReason Reason { get; set; }
}

/// <summary>Request for POST api/clients/hostnames/enabled.</summary>
public class SetClientHostnameLookupRequest
{
    public bool Enabled { get; set; }
}

/// <summary>Response for POST api/clients/hostnames/enabled: the toggle that was persisted.</summary>
public class SetClientHostnameLookupResponse
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Why a forward lookup found no address for the name it was given. Deliberately narrower than
/// <see cref="ClientHostnamesReason"/>: one name is asked about at a time, so there is no partial
/// batch, no cap and nothing left running after the answer.
/// </summary>
[JsonConverter(typeof(ClientAddressLookupReasonJsonConverter))]
public enum ClientAddressLookupReason
{
    /// <summary>Addresses were found; there is nothing to explain.</summary>
    None,

    /// <summary>The DNS server answered, and the name has no address record.</summary>
    NoRecords,

    /// <summary>
    /// No lancache DNS server could be found to ask, so the query went to the container's own
    /// system resolver, which does not carry the LAN's names.
    /// </summary>
    NoResolver,

    /// <summary>The DNS server did not answer in time.</summary>
    ResolverTimeout
}

internal sealed class ClientAddressLookupReasonJsonConverter : JsonStringEnumConverter<ClientAddressLookupReason>
{
    public ClientAddressLookupReasonJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// What one forward lookup established: every address the network publishes for the name, and why
/// there is none when the list is empty.
/// </summary>
public sealed record ClientAddressLookupOutcome(
    IReadOnlyList<string> Addresses,
    ClientAddressLookupReason Reason);

/// <summary>Request for POST api/clients/hostnames/resolve.</summary>
public class ResolveClientAddressRequest
{
    public string Hostname { get; set; } = string.Empty;
}

/// <summary>
/// Response for POST api/clients/hostnames/resolve: the name as it was asked about, every address
/// the network publishes for it, and why the list is empty when it is.
/// </summary>
public class ResolveClientAddressResponse
{
    public string Hostname { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public ClientAddressLookupReason Reason { get; set; }
}
