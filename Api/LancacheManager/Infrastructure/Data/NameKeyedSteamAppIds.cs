using System.Reflection;
using System.Text.Json;

namespace LancacheManager.Infrastructure.Data;

/// <summary>
/// Single-source <c>GameName -&gt; Steam appId</c> lookup for name-keyed services (Blizzard/Riot)
/// whose games ALSO exist on Steam. Backed by the embedded <c>blizzard_steam_appids.json</c>
/// resource. Consulted before the curated embedded banner so a Steam-mapped Blizzard/Riot game
/// renders Steam's <c>header.jpg</c> ("Steam-first, embedded fallback").
/// Keys are produced by <see cref="NameKeyedBannerSource.Slug"/> and the service is normalized via
/// <see cref="NameKeyedBannerSource.NormalizeService"/> so this map, the curated banner map, the
/// serve route, and the stored GameImage AppId can never drift.
/// </summary>
public static class NameKeyedSteamAppIds
{
    private const string ResourceName = "LancacheManager.blizzard_steam_appids.json";

    // (normalized service, slug) -> Steam appId. Slug is the normalized GameName.
    private static readonly Lazy<Dictionary<(string Service, string Slug), long>> _bySlug =
        new(LoadBySlug);

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
        }) ?? throw new InvalidOperationException("Embedded blizzard_steam_appids.json is malformed");

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
