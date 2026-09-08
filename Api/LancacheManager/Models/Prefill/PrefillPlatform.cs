using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(JsonStringEnumConverter<PrefillPlatform>))]
public enum PrefillPlatform
{
    Steam,
    Epic,
    BattleNet,
    Riot,
    Xbox
}

public static class PrefillPlatformExtensions
{
    public static string ToService(this PrefillPlatform platform) => platform switch
    {
        PrefillPlatform.Steam => "steam",
        PrefillPlatform.Epic => "epicgames",
        PrefillPlatform.BattleNet => "blizzard",
        PrefillPlatform.Riot => "riot",
        PrefillPlatform.Xbox => "xbox",
        _ => throw new ArgumentOutOfRangeException(nameof(platform))
    };

    public static PrefillPlatform? ToPrefillPlatform(this string service) => service.ToLowerInvariant() switch
    {
        "steam" => PrefillPlatform.Steam,
        "epicgames" or "epic" => PrefillPlatform.Epic,
        "blizzard" or "battlenet" => PrefillPlatform.BattleNet,
        "riot" => PrefillPlatform.Riot,
        "xbox" => PrefillPlatform.Xbox,
        _ => null
    };
    /// <summary>
    /// Whether prefilling this platform needs an account. Battle.net and Riot pull public CDN content
    /// anonymously - their daemons override <c>InitialAuthState</c> to Authenticated and report
    /// themselves logged in as soon as they connect - so a readiness check that fails for them means
    /// the container is not up yet, never that a login is missing. Callers that gate on login state
    /// must ask this first, or they tell a Battle.net user to sign in to something that has no sign-in.
    /// </summary>
    public static bool RequiresLogin(this PrefillPlatform platform)
        => platform is not (PrefillPlatform.BattleNet or PrefillPlatform.Riot);
}
