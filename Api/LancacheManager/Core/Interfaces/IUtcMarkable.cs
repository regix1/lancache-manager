namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Marker interface for entities that need UTC DateTime marking after retrieval from database.
/// Marks an entity's DateTimes as UTC for the paths that do not come back from a query, where
/// Kind arrives unset and would otherwise be read as local time.
/// </summary>
public interface IUtcMarkable
{
    /// <summary>
    /// Marks all DateTime properties on this entity as UTC.
    /// Call this after retrieving entities from the database.
    /// </summary>
    void MarkDateTimesAsUtc();
}
