namespace LancacheManager.Configuration;

/// <summary>
/// Configuration options for API behavior
/// </summary>
public class ApiOptions
{
    /// <summary>
    /// Maximum number of clients that can be requested in a single API call.
    /// Requests exceeding this limit will be capped to this value.
    /// </summary>
    public int MaxClientsPerRequest { get; set; } = 1000;

    /// <summary>
    /// Default number of clients to return when no limit is specified.
    /// </summary>
    public int DefaultClientsLimit { get; set; } = 100;
}
