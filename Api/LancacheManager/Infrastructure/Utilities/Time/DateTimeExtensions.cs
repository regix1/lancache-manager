using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Extension methods for DateTime to simplify UTC timezone handling. Npgsql already hands back
/// timestamptz columns with Kind set to Utc, so these are for the values that do not come from a
/// query: anything deserialized, constructed in memory, or read out of a settings file arrives
/// with Kind unset, and comparing that against a UTC value silently shifts it.
/// </summary>
public static class DateTimeExtensions
{
    // ===== UTC Marking Methods =====

    /// <summary>
    /// Marks a DateTime as UTC. Use on a value whose Kind is unset but which is known to already
    /// hold a UTC instant; this labels it rather than converting it.
    /// </summary>
    public static DateTime AsUtc(this DateTime dt)
        => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    // ===== Unix Timestamp Conversion Methods =====

    /// <summary>
    /// Converts Unix timestamp (seconds since epoch) to UTC DateTime.
    /// </summary>
    public static DateTime FromUnixSeconds(this long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;

    /// <summary>
    /// Marks a nullable DateTime as UTC. Returns null if input is null.
    /// </summary>
    public static DateTime? AsUtc(this DateTime? dt)
        => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : null;

    /// <summary>
    /// Marks all DateTime properties as UTC for a single entity that implements IUtcMarkable.
    /// Returns the same entity for fluent chaining.
    /// </summary>
    public static T WithUtcMarking<T>(this T entity) where T : IUtcMarkable
    {
        entity.MarkDateTimesAsUtc();
        return entity;
    }

    /// <summary>
    /// Marks all DateTime properties as UTC for a collection of entities.
    /// Returns the same list for fluent chaining.
    /// </summary>
    public static List<T> WithUtcMarking<T>(this List<T> entities) where T : IUtcMarkable
    {
        foreach (var entity in entities)
        {
            entity.MarkDateTimesAsUtc();
        }
        return entities;
    }

    /// <summary>
    /// Marks all DateTime properties as UTC for an IEnumerable of entities.
    /// Materializes to a List and returns it.
    /// </summary>
    public static List<T> WithUtcMarking<T>(this IEnumerable<T> entities) where T : IUtcMarkable
    {
        var list = entities.ToList();
        foreach (var entity in list)
        {
            entity.MarkDateTimesAsUtc();
        }
        return list;
    }
}
