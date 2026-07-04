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

    internal static bool IsLancacheCacheContainer(string image, IEnumerable<string> names) =>
        _lancacheImagePrefixes.Any(p => image.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
        // Forks of lancachenet/monolithic keep "monolithic" in the image name but not the
        // "lancachenet/" owner prefix - match the segment so a fork image still classifies.
        image.Contains("monolithic", StringComparison.OrdinalIgnoreCase) ||
        names.Any(n =>
            n.Contains("monolithic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("lancache", StringComparison.OrdinalIgnoreCase));
}
