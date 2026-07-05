namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// The user-selectable DNS resolver mode for the Status Check sweep. Plain strings (matching the
/// project's plain-string status style, persisted verbatim in AppState) rather than a C# enum:
/// <list type="bullet">
/// <item><c>auto</c> - probe ALL candidates (bridge dns-container IP, default gateway, known cache
/// IPs, host.docker.internal, loopback) and heartbeat-verify. Default.</item>
/// <item><c>bridge</c> - ONLY the lancache-dns container's bridge IP (Docker inspect).</item>
/// <item><c>host</c> - ONLY host-side candidates (gateway, known cache IPs, host.docker.internal,
/// loopback); the Docker bridge-container path is skipped.</item>
/// </list>
/// An explicit <c>Prefill__LancacheDnsIp</c> override still wins in every mode (it is the hard ops
/// override and is honored before any mode branching).
/// </summary>
public static class StatusCheckResolverModes
{
    public const string Auto = "auto";
    public const string Bridge = "bridge";
    public const string Host = "host";

    /// <summary>All valid modes, in wire order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Auto, Bridge, Host };

    /// <summary>True when <paramref name="mode"/> is one of the three allowed values.</summary>
    public static bool IsValid(string? mode) =>
        mode is Auto or Bridge or Host;

    /// <summary>Normalizes an arbitrary/absent stored value to a valid mode, defaulting unknown or
    /// empty input to <see cref="Auto"/> (used on read so a corrupt persisted value never breaks the sweep).</summary>
    public static string Normalize(string? mode) => IsValid(mode) ? mode! : Auto;
}
