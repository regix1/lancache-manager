namespace LancacheManager.Models;

/// <summary>
/// Strongly-typed options bound from the <c>Prefill</c> configuration section.
/// Backed by environment variables of the form <c>Prefill__&lt;Property&gt;</c>.
/// </summary>
/// <remarks>
/// <para><see cref="LancacheIp"/> is the lancache server (nginx) IP/hostname, forwarded to the
/// daemon container as the <c>LANCACHE_IP</c> env var so the daemon bypasses container DNS for
/// CDN traffic.</para>
/// <para><see cref="LancacheDnsIp"/> is the legacy DNS-server IP applied to <c>HostConfig.DNS</c>
/// in bridge mode only. It is independent of <see cref="LancacheIp"/> — they may be used together
/// or independently.</para>
/// </remarks>
public sealed class PrefillNetworkOptions
{
    /// <summary>
    /// Network mode applied to the daemon container. One of: "host", "bridge", "auto",
    /// a custom Docker network name, or null (default).
    /// </summary>
    public string? NetworkMode { get; init; }

    /// <summary>
    /// Lancache server IP literal or hostname, forwarded to the daemon container as
    /// <c>LANCACHE_IP</c>. When set, the daemon ignores container DNS for CDN traffic
    /// and routes directly to this address with explicit Host headers.
    /// </summary>
    public string? LancacheIp { get; init; }

    /// <summary>
    /// Legacy DNS-server IP (e.g. lancache-dns or AdGuard Home) applied to
    /// <c>HostConfig.DNS</c> in bridge mode only. Ignored in host networking mode
    /// (Docker drops <c>HostConfig.DNS</c> when <c>NetworkMode=host</c>).
    /// </summary>
    public string? LancacheDnsIp { get; init; }

    /// <summary>
    /// When true, the daemon communicates with the manager over TCP instead of a Unix
    /// domain socket. Defaults to TCP on Windows and socket on Linux when null.
    /// </summary>
    public bool? UseTcp { get; init; }
}
