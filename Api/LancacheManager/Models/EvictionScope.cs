namespace LancacheManager.Models;

/// <summary>
/// Discriminates the entity type for partial-eviction removal operations.
/// </summary>
public enum EvictionScope
{
    /// <summary>Steam game — keyed by GameAppId (long).</summary>
    Steam,

    /// <summary>Epic Games game — keyed by EpicAppId (string).</summary>
    Epic,

    /// <summary>Non-game service — keyed by service name (lowercased).</summary>
    Service
}
