using System.Globalization;

namespace LancacheManager.Models;

/// <summary>
/// Stable, canonical identifier for a cache-state conflict target ("what does this operation touch?").
/// Used by <c>IOperationConflictChecker</c> to compare a new request against currently-active operations.
///
/// Canonicalization rules (applied at both register-time and lookup-time):
/// <list type="bullet">
///   <item><description><c>Kind="steam"</c>, <c>Key=appId.ToString(InvariantCulture)</c></description></item>
///   <item><description><c>Kind="epic"</c>,  <c>Key=epicAppId ?? gameName</c> (case-sensitive — Epic slugs are lowercase)</description></item>
///   <item><description><c>Kind="service"</c>, <c>Key=serviceName.ToLowerInvariant()</c></description></item>
///   <item><description><c>Kind="bulk"</c>,  <c>Key=""</c> (sentinel — bulk never <see cref="Matches"/> any entity)</description></item>
/// </list>
/// </summary>
public readonly record struct ConflictScope(string Kind, string Key)
{
    public static ConflictScope SteamGame(long appId) => new("steam", appId.ToString(CultureInfo.InvariantCulture));
    public static ConflictScope EpicGame(string? epicAppId, string gameName) => new("epic", epicAppId ?? gameName);
    public static ConflictScope Service(string serviceName) => new("service", serviceName.ToLowerInvariant());
    public static ConflictScope Bulk() => new("bulk", string.Empty);

    /// <summary>
    /// True if the two scopes refer to the SAME entity (same kind + same key, case-sensitive).
    /// </summary>
    public bool Matches(ConflictScope other) =>
        Kind == other.Kind && string.Equals(Key, other.Key, StringComparison.Ordinal);

    /// <summary>
    /// True if <c>this</c> is a service-level scope that covers <paramref name="other"/>
    /// (a steam/epic game belonging to that service). The caller must pass the game's service name
    /// (derivable from <see cref="Kind"/>: "steam" → "steam", "epic" → "epicgames").
    /// </summary>
    public bool Covers(ConflictScope other, string? otherGameService) =>
        Kind == "service" && otherGameService != null &&
        string.Equals(Key, otherGameService.ToLowerInvariant(), StringComparison.Ordinal);

    /// <summary>
    /// Canonical "<c>kind:key</c>" string used as the secondary key in
    /// <c>UnifiedOperationTracker._entityKeyIndex</c>. Prefixing with kind prevents
    /// ambiguity between e.g. <c>ServiceRemoval svc="steam"</c> and <c>GameRemoval steam:?</c>.
    /// </summary>
    public string ToTrackerKey() => $"{Kind}:{Key}";
}
