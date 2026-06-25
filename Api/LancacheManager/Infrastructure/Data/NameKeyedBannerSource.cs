using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Curated <c>GameName -&gt; official-CDN banner URL</c> lookup for name-keyed services
/// (Blizzard/Battle.net and Riot) that have no Steam appId or Epic catalog id and therefore
/// cannot use a CDN-by-id guess. Backed by the embedded <c>game_banners.json</c> resource
/// (official publisher CDNs only). The stored GameImage AppId and the controller
/// route slug are produced by <see cref="Slug"/> so backend storage, the serve route, and the
/// frontend request URL all agree.
/// </summary>
public static class NameKeyedBannerSource
{
    private const string ResourceName = "LancacheManager.game_banners.json";

    /// <summary>
    /// Sentinel scheme used in <c>game_banners.json</c> values. A value of
    /// <c>embedded://{slug}</c> means the banner bytes come from the embedded resource
    /// <c>LancacheManager.banners.{slug}.jpg</c> and must NEVER be fetched over the network.
    /// </summary>
    private const string EmbeddedScheme = "embedded://";

    /// <summary>Canonical service keys this source covers.</summary>
    public const string BlizzardService = "blizzard";
    public const string RiotService = "riot";
    public const string XboxService = "xbox";

    // (service, slug) -> source url. Slug is the normalized GameName.
    private static readonly Lazy<Dictionary<(string Service, string Slug), string>> _bySlug =
        new(LoadBySlug);

    // Curated slugs whose banner is an embedded:// sentinel AND whose JPEG resource is present in
    // the assembly. These are served on-demand (no stored GameImage row needed), so the /available
    // endpoint advertises them and the serve route resolves them instantly.
    private static readonly Lazy<HashSet<string>> _embeddedSlugs =
        new(LoadEmbeddedSlugs);

    // Set of curated service keys (the top-level sections of game_banners.json), so NormalizeService
    // recognizes any name-keyed service present in the data with no code change.
    private static readonly Lazy<HashSet<string>> _services =
        new(() => _bySlug.Value.Keys.Select(k => k.Service).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// Normalizes a service string to one of the canonical keys this source covers,
    /// or null if the service is not name-keyed here.
    /// </summary>
    public static string? NormalizeService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        var lower = service.ToLowerInvariant();
        return lower switch
        {
            // Well-known aliases mapping an nginx/request service name to its game_banners.json key.
            "blizzard" or "battle.net" or "battlenet" => BlizzardService,
            "riot" or "riotgames" => RiotService,
            // Xbox is name-keyed but its banners come from the DisplayCatalog at runtime (NOT the
            // curated game_banners.json / embedded JPEGs), so it is recognized by alias here even
            // though it has no curated section. Its GameImage rows are fetched + stored dynamically.
            "xbox" or "xboxlive" or "microsoft" => XboxService,
            // Any OTHER service with a curated section in game_banners.json is name-keyed too, so a
            // new name-keyed service needs only a JSON section + embedded JPEGs (no code change).
            _ => _services.Value.Contains(lower) ? lower : null
        };
    }

    /// <summary>
    /// Produces a stable, URL-safe slug from a GameName: lowercase, non-alphanumeric runs
    /// collapsed to single hyphens, leading/trailing hyphens trimmed. Used identically for the
    /// stored AppId, the controller route segment, and the frontend request URL.
    /// </summary>
    public static string Slug(string gameName)
    {
        var sb = new StringBuilder(gameName.Length);
        var lastWasHyphen = false;
        foreach (var ch in gameName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        return sb.ToString().Trim('-');
    }

    /// <summary>
    /// Resolves the official banner URL for a name-keyed (service, gameName). Returns null when
    /// the service is not name-keyed, the name is empty, or no curated entry exists (no fallback
    /// placeholder URL - a missing entry means no banner is fetched).
    /// </summary>
    public static string? TryGetUrl(string? service, string? gameName)
    {
        var normalized = NormalizeService(service);
        if (normalized == null || string.IsNullOrWhiteSpace(gameName)) return null;

        return _bySlug.Value.TryGetValue((normalized, Slug(gameName)), out var url) ? url : null;
    }

    /// <summary>
    /// Resolves an <c>embedded://{slug}</c> sentinel URL to the hard-coded banner bytes embedded as
    /// <c>LancacheManager.banners.{slug}.jpg</c>. Returns true with the JPEG bytes and
    /// <c>image/jpeg</c> content type when the url is an embedded sentinel whose resource exists;
    /// returns false for any non-embedded url (so the caller can fall through to a network fetch).
    /// This is the sole source for the 20 name-keyed banners - they are never fetched at runtime.
    /// </summary>
    public static bool TryGetEmbeddedBytes(string url, out byte[] bytes, out string contentType)
    {
        bytes = Array.Empty<byte>();
        contentType = "image/jpeg";

        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith(EmbeddedScheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var slug = url.Substring(EmbeddedScheme.Length).Trim();
        if (slug.Length == 0) return false;

        var resourceName = $"LancacheManager.banners.{slug}.jpg";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return false;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        bytes = ms.ToArray();
        return bytes.Length > 0;
    }

    /// <summary>
    /// Resolves a curated name-keyed (service, slug) DIRECTLY to its embedded banner bytes, without
    /// needing a stored GameImage row. Lets the serve route return curated Blizzard/Riot banners
    /// instantly (the JPEGs live in the assembly). Returns false when the service is not name-keyed,
    /// no curated entry exists for the slug, or that entry is not an <c>embedded://</c> sentinel.
    /// </summary>
    public static bool TryGetEmbeddedBytesForSlug(string? service, string? slug, out byte[] bytes, out string contentType)
    {
        bytes = Array.Empty<byte>();
        contentType = "image/jpeg";

        var normalized = NormalizeService(service);
        if (normalized == null || string.IsNullOrWhiteSpace(slug)) return false;

        return _bySlug.Value.TryGetValue((normalized, slug!), out var url)
            && TryGetEmbeddedBytes(url, out bytes, out contentType);
    }

    /// <summary>
    /// All curated name-keyed slugs whose banner is an embedded:// sentinel with a present JPEG
    /// resource. The /available endpoint reports these so the frontend renders a curated game's
    /// banner the instant its card appears, with no fetched GameImage row required.
    /// </summary>
    public static IReadOnlyCollection<string> EmbeddedBannerSlugs() => _embeddedSlugs.Value;

    private static Dictionary<(string, string), string> LoadBySlug()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded banner map resource '{ResourceName}' not found. " +
                "Verify the EmbeddedResource link in LancacheManager.csproj.");

        // Parsed generically as { service -> { gameName -> url } } so ANY name-keyed service in the
        // JSON is loaded with no code change. Non-object top-level values (e.g. the "_comment" doc
        // string, or any future metadata key) are skipped.
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Embedded game_banners.json is malformed");

        var result = new Dictionary<(string, string), string>();

        foreach (var (service, section) in raw)
        {
            if (section.ValueKind != JsonValueKind.Object) continue;

            var svc = service.ToLowerInvariant();
            foreach (var game in section.EnumerateObject())
            {
                if (game.Value.ValueKind != JsonValueKind.String) continue;
                var url = game.Value.GetString();
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(game.Name)) continue;
                result[(svc, Slug(game.Name))] = url;
            }
        }

        return result;
    }

    private static HashSet<string> LoadEmbeddedSlugs()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceNames = new HashSet<string>(assembly.GetManifestResourceNames(), StringComparer.Ordinal);

        var slugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((_, slug), url) in _bySlug.Value)
        {
            if (url.StartsWith(EmbeddedScheme, StringComparison.OrdinalIgnoreCase)
                && resourceNames.Contains($"LancacheManager.banners.{slug}.jpg"))
            {
                slugs.Add(slug);
            }
        }

        return slugs;
    }
}
