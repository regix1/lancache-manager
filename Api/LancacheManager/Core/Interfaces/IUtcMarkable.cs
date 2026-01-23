namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Marker interface for entities that need UTC DateTime marking after retrieval from database.
/// SQLite doesn't preserve DateTimeKind, so we need to mark retrieved DateTimes as UTC.
/// </summary>
public interface IUtcMarkable
{
    /// <summary>
    /// Marks all DateTime properties on this entity as UTC.
    /// Call this after retrieving entities from the database.
    /// </summary>
    void MarkDateTimesAsUtc();
}
