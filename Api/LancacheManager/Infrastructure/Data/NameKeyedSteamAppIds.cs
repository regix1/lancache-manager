using System.Reflection;
using System.Text.Json;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Single-source <c>GameName -&gt; Steam appId</c> lookup for name-keyed services whose games ALSO
/// exist on Steam. Backed by the embedded <c>steam_fallback_appids.json</c> resource, which is
/// service-scoped so any service can opt a game into "Steam-first": that game's Steam
/// <c>header.jpg</c> wins over the service's own art.
/// For services normalized by <see cref="NameKeyedBannerSource.NormalizeService"/> (Blizzard, Xbox,
/// Riot), keys are produced by <see cref="NameKeyedBannerSource.Slug"/> and the service is
/// normalized so this map, the curated banner map, the serve route, and the stored GameImage AppId
/// can never drift. Epic is appId-keyed rather than name-keyed and never reaches
/// <see cref="NameKeyedBannerSource.NormalizeService"/>; it resolves its own "epic" section via
/// <see cref="TryGetSteamAppIdForSection"/> instead.
/// </summary>
public static class NameKeyedSteamAppIds
{
    private const string ResourceName = "LancacheManager.steam_fallback_appids.json";

    // (normalized service, slug) -> Steam appId. Slug is the normalized GameName.
    private static readonly Lazy<Dictionary<(string Service, string Slug), long>> _bySlug =
        new(LoadBySlug);

    /// <summary>
    /// Every curated (service, slug) that resolves to a Steam appId. The serve route answers such a
    /// slug from Steam's stored header even though nothing is stored under the slug itself, so
    /// /available has to report those slugs too. Without this it advertised the Steam appId alone,
    /// while the row asking whether a banner exists asks by slug, and the banner never rendered. [50]
    /// </summary>
    public static IEnumerable<(string Service, string Slug, long SteamAppId)> SteamBackedSlugs()
        => _bySlug.Value.Select(entry => (entry.Key.Service, entry.Key.Slug, entry.Value));

    /// <summary>
    /// Resolves the Steam appId for a name-keyed (service, gameName), or null when the service is
    /// not name-keyed, the name is empty, or no Steam mapping exists for that game (in which case
    /// the caller falls back to the curated embedded banner).
    /// </summary>
    public static long? TryGetSteamAppId(string? service, string? gameName)
    {
        var normalized = NameKeyedBannerSource.NormalizeService(service);
        if (normalized == null || string.IsNullOrWhiteSpace(gameName)) return null;

        return _bySlug.Value.TryGetValue((normalized, NameKeyedBannerSource.Slug(gameName)), out var appId)
            ? appId
            : null;
    }

    /// <summary>
    /// Resolves the Steam appId for a name-keyed (service, slug) directly, where the slug is the
    /// normalized GameName produced by <see cref="NameKeyedBannerSource.Slug"/>. Used by the serve
    /// route to decide Steam-first without re-deriving a GameName. Returns null when the service is
    /// not name-keyed or no Steam mapping exists for that slug.
    /// </summary>
    public static long? TryGetSteamAppIdBySlug(string? service, string? slug)
    {
        var normalized = NameKeyedBannerSource.NormalizeService(service);
        if (normalized == null || string.IsNullOrWhiteSpace(slug)) return null;

        return _bySlug.Value.TryGetValue((normalized, slug!.ToLowerInvariant()), out var appId)
            ? appId
            : null;
    }

    /// <summary>
    /// Resolves the Steam appId for a raw JSON section key directly, bypassing
    /// <see cref="NameKeyedBannerSource.NormalizeService"/>. For services that are keyed by their own
    /// catalog id (e.g. Epic's EpicAppId) rather than by GameName, so they never reach the
    /// name-keyed banner family / NormalizeService gate that blizzard/riot/xbox go through.
    /// <paramref name="section"/> must match the literal top-level key used in
    /// <c>steam_fallback_appids.json</c> (case-insensitive).
    /// </summary>
    public static long? TryGetSteamAppIdForSection(string section, string? gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) return null;

        return _bySlug.Value.TryGetValue((section.ToLowerInvariant(), NameKeyedBannerSource.Slug(gameName)), out var appId)
            ? appId
            : null;
    }

    private static Dictionary<(string, string), long> LoadBySlug()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Steam appId map resource '{ResourceName}' not found. " +
                "Verify the EmbeddedResource link in LancacheManager.csproj.");

        // Parsed generically as { service -> { gameName -> appId } } so ANY name-keyed service in
        // the JSON is loaded with no code change. Non-object top-level values (e.g. the "_comment"
        // doc string, or any future metadata key) are skipped.
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Embedded steam_fallback_appids.json is malformed");

        var result = new Dictionary<(string, string), long>();

        foreach (var (service, section) in raw)
        {
            if (section.ValueKind != JsonValueKind.Object) continue;

            var svc = service.ToLowerInvariant();
            foreach (var game in section.EnumerateObject())
            {
                if (game.Value.ValueKind != JsonValueKind.Number) continue;
                if (string.IsNullOrWhiteSpace(game.Name)) continue;
                if (!game.Value.TryGetInt64(out var appId) || appId <= 0) continue;
                result[(svc, NameKeyedBannerSource.Slug(game.Name))] = appId;
            }
        }

        return result;
    }
}
