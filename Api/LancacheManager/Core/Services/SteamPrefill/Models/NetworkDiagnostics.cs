namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Result of a DNS resolution test for a single domain
/// </summary>
public class DnsTestResult
{
    public string Domain { get; set; } = string.Empty;
    public List<string> ResolvedIps { get; set; } = new();
    public bool IsPrivateIp { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Network diagnostics results for a prefill container
/// </summary>
public class NetworkDiagnostics
{
    public bool InternetConnectivity { get; set; }
    public string? InternetConnectivityError { get; set; }
    public bool? InternetConnectivityIpv4 { get; set; }
    public string? InternetConnectivityIpv4Error { get; set; }
    public bool? InternetConnectivityIpv6 { get; set; }
    public string? InternetConnectivityIpv6Error { get; set; }
    public List<DnsTestResult> DnsResults { get; set; } = new();
    public DateTime TestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True if the container is using host networking mode.
    /// When using host networking with transparent proxy setups, DNS may resolve to public IPs
    /// but traffic still routes through lancache via router-level interception.
    /// </summary>
    public bool UseHostNetworking { get; set; }

    /// <summary>
    /// The lancache server IP that was injected into the daemon container via the
    /// <c>LANCACHE_IP</c> environment variable. Null when no cache IP could be determined.
    /// </summary>
    public string? LancacheIpInjected { get; set; }

    /// <summary>
    /// How the injected lancache IP was located: <c>config</c> | <c>dockerInspect</c> |
    /// <c>envFile</c> | <c>detected</c> | <c>none</c>. Lets the frontend report a positive
    /// detected-source ("auto-detected at X, heartbeat verified") instead of a generic
    /// resolution-failed warning. <c>null</c>/<c>none</c> when no cache IP was determined.
    /// </summary>
    public string? LancacheIpSource { get; set; }
}
