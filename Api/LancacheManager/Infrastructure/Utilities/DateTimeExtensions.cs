namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Extension methods for DateTime to simplify UTC timezone handling.
/// SQLite doesn't preserve DateTimeKind, so we need to explicitly mark DateTimes as UTC after reading from DB.
/// </summary>
public static class DateTimeExtensions
{
    /// <summary>
    /// Marks a DateTime as UTC. Use after reading from SQLite which doesn't preserve DateTimeKind.
    /// </summary>
    public static DateTime AsUtc(this DateTime dt)
        => DateTime.SpecifyKind(dt, DateTimeKind.Utc);

    /// <summary>
    /// Marks a nullable DateTime as UTC. Returns null if input is null.
    /// </summary>
    public static DateTime? AsUtc(this DateTime? dt)
        => dt.HasValue ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc) : null;
}
