namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Shared container name/image classification used by both <see cref="LancacheServerLocator"/>
/// (cache-IP + lancache-dns-IP auto-detection) and <see cref="LancacheEnvironmentSource"/>
/// (Docker-inspect env tier) - factored out so the two Docker-scanning services agree on exactly
/// which running container is "the manager", "the DNS container", and "the cache container"
/// instead of drifting apart with copy-pasted predicates.
/// </summary>
internal static class DockerContainerMatching
{
    private static readonly string[] _lancacheImagePrefixes =
    {
        "lancachenet/monolithic", "lancachenet/generic", "lancachenet/sniproxy"
    };

    internal static bool IsManagerContainer(IEnumerable<string> names, string image) =>
        names.Any(n => n.Contains("manager", StringComparison.OrdinalIgnoreCase)) ||
        image.Contains("lancache-manager", StringComparison.OrdinalIgnoreCase);

    internal static bool IsDnsContainer(IEnumerable<string> names) =>
        names.Any(n =>
            n.Contains("lancache-dns", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("lancachedns", StringComparison.OrdinalIgnoreCase) ||
            (n.Contains("dns", StringComparison.OrdinalIgnoreCase) && n.Contains("lancache", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Other LAN resolvers that may hold reverse records lancache-dns does not: AdGuard, unbound,
    /// Pi-hole, blocky, Technitium. Distinct from <see cref="IsDnsContainer"/> so Status Check and
    /// prefill never treat one of these as the cache DNS.
    /// </summary>
    internal static bool IsLanResolverContainer(IEnumerable<string> names, string image)
    {
        foreach (var value in names.Append(image ?? string.Empty))
        {
            if (value.Contains("adguard", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("unbound", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("pihole", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("pi-hole", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("blocky", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("technitium", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Image-only half of the cache-container test. Split out so callers that apply their own
    /// name filtering (for example excluding the DNS and sniproxy containers) can still share the
    /// image rules instead of restating them.
    /// </summary>
    internal static bool IsLancacheCacheImage(string image) =>
        _lancacheImagePrefixes.Any(p => image.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
        // Forks of lancachenet/monolithic keep "monolithic" in the image name but not the
        // "lancachenet/" owner prefix - match the segment so a fork image still classifies.
        image.Contains("monolithic", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLancacheCacheContainer(string image, IEnumerable<string> names) =>
        IsLancacheCacheImage(image) ||
        names.Any(n =>
            n.Contains("monolithic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("lancache", StringComparison.OrdinalIgnoreCase));
}
