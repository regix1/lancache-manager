namespace LancacheManager.Models.Responses;

/// <summary>
/// Response for GET api/clients/hostnames: whether the lookup is on, plus a name for each client
/// address the network published one for. Names carry no trailing dot, and an address with no
/// name is simply absent from the map.
/// </summary>
public class ClientHostnamesResponse
{
    public bool Enabled { get; set; }
    public Dictionary<string, string> Hostnames { get; set; } = new();
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
