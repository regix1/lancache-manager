namespace LancacheManager.Models;

/// <summary>
/// Discriminates the entity type for partial-eviction removal operations.
/// </summary>
public enum EvictionScope
{
    /// <summary>Steam game - keyed by GameAppId (long).</summary>
    Steam,

    /// <summary>Epic Games game - keyed by EpicAppId (string).</summary>
    Epic,

    /// <summary>
    /// Named (Blizzard/Riot) game - no GameAppId/EpicAppId; identified by
    /// (Service, GameName). The eviction <c>key</c> carries the lowercased service
    /// name and the game name travels alongside as a separate argument.
    /// </summary>
    Named,

    /// <summary>Non-game service - keyed by service name (lowercased).</summary>
    Service
}
