namespace LancacheManager.Infrastructure.Extensions;

/// <summary>
/// Extension methods for string operations
/// </summary>
public static class StringExtensions
{
    /// <summary>
    /// Returns true if the string is null, empty, or whitespace.
    /// </summary>
    public static bool IsEmpty(this string? value)
        => string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Returns true if the string has content (not null, empty, or whitespace).
    /// </summary>
    public static bool HasValue(this string? value)
        => !string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// Returns the value or a default if null/empty.
    /// </summary>
    public static string OrDefault(this string? value, string defaultValue)
        => value.HasValue() ? value! : defaultValue;

    /// <summary>
    /// Returns null if the string is empty or whitespace, otherwise returns the string.
    /// </summary>
    public static string? NullIfEmpty(this string? value)
        => value.HasValue() ? value : null;

    /// <summary>
    /// Truncates string to specified length, adding ellipsis if truncated.
    /// </summary>
    public static string Truncate(this string value, int maxLength, string suffix = "...")
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - suffix.Length)] + suffix;
    }
}
