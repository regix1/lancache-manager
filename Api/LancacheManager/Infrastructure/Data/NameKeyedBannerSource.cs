using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // (service, slug) -> source url. Slug is the normalized GameName.
    private static readonly Lazy<Dictionary<(string Service, string Slug), string>> _bySlug =
        new(LoadBySlug);

    /// <summary>
    /// Normalizes a service string to one of the canonical keys this source covers,
    /// or null if the service is not name-keyed here.
    /// </summary>
    public static string? NormalizeService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service)) return null;
        return service.ToLowerInvariant() switch
        {
            "blizzard" or "battle.net" or "battlenet" => BlizzardService,
            "riot" or "riotgames" => RiotService,
            _ => null
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

    private static Dictionary<(string, string), string> LoadBySlug()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded banner map resource '{ResourceName}' not found. " +
                "Verify the EmbeddedResource link in LancacheManager.csproj.");

        var raw = JsonSerializer.Deserialize<RawBannerMap>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Embedded game_banners.json is malformed");

        var result = new Dictionary<(string, string), string>();

        void AddSection(string service, Dictionary<string, string>? section)
        {
            if (section == null) return;
            foreach (var (gameName, url) in section)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                result[(service, Slug(gameName))] = url;
            }
        }

        AddSection(BlizzardService, raw.Blizzard);
        AddSection(RiotService, raw.Riot);

        return result;
    }

    private sealed class RawBannerMap
    {
        [JsonPropertyName("blizzard")]
        public Dictionary<string, string>? Blizzard { get; set; }

        [JsonPropertyName("riot")]
        public Dictionary<string, string>? Riot { get; set; }
    }
}
