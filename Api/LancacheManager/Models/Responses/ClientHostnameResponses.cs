using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models.Responses;

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
    StillLooking
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
