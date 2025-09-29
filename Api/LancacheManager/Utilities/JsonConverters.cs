using System.Text.Json;

namespace LancacheManager.Utilities;

/// <summary>
/// Utility class for safe JSON type conversions
/// </summary>
public static class JsonConverters
{
    /// <summary>
    /// Safely converts a JSON value to Int32
    /// </summary>
    public static int ToInt32Safe(object? obj)
    {
        if (obj == null) return 0;

        if (obj is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Number)
            {
                if (jsonElement.TryGetInt32(out var intValue))
                {
                    return intValue;
                }
                if (jsonElement.TryGetInt64(out var longValue))
                {
                    return (int)longValue;
                }
                if (jsonElement.TryGetDouble(out var doubleValue))
                {
                    return (int)doubleValue;
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.String)
            {
                if (int.TryParse(jsonElement.GetString(), out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }
        else if (obj is int i)
        {
            return i;
        }
        else if (obj is long l)
        {
            return (int)l;
        }
        else if (obj is double d)
        {
            return (int)d;
        }
        else if (obj is string s && int.TryParse(s, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>
    /// Safely converts a JSON value to Int64
    /// </summary>
    public static long ToInt64Safe(object? obj)
    {
        if (obj == null) return 0;

        if (obj is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Number)
            {
                if (jsonElement.TryGetInt64(out var longValue))
                {
                    return longValue;
                }
                if (jsonElement.TryGetInt32(out var intValue))
                {
                    return intValue;
                }
                if (jsonElement.TryGetDouble(out var doubleValue))
                {
                    return (long)doubleValue;
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.String)
            {
                if (long.TryParse(jsonElement.GetString(), out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }
        else if (obj is long l)
        {
            return l;
        }
        else if (obj is int i)
        {
            return i;
        }
        else if (obj is double d)
        {
            return (long)d;
        }
        else if (obj is string s && long.TryParse(s, out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>
    /// Safely converts a JSON value to Double
    /// </summary>
    public static double ToDoubleSafe(object? obj)
    {
        if (obj == null) return 0.0;

        if (obj is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Number)
            {
                if (jsonElement.TryGetDouble(out var doubleValue))
                {
                    return doubleValue;
                }
                if (jsonElement.TryGetInt64(out var longValue))
                {
                    return longValue;
                }
                if (jsonElement.TryGetInt32(out var intValue))
                {
                    return intValue;
                }
            }
            else if (jsonElement.ValueKind == JsonValueKind.String)
            {
                if (double.TryParse(jsonElement.GetString(), out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }
        else if (obj is double d)
        {
            return d;
        }
        else if (obj is float f)
        {
            return f;
        }
        else if (obj is long l)
        {
            return l;
        }
        else if (obj is int i)
        {
            return i;
        }
        else if (obj is string s && double.TryParse(s, out var parsed))
        {
            return parsed;
        }

        return 0.0;
    }
}