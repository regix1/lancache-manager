using System.Globalization;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Parses cache-size values used by lancache configuration and datasource overrides.
/// Units use binary multiples to match CACHE_DISK_SIZE behavior.
/// </summary>
public static class CacheSizeParser
{
    /// <summary>
    /// Converts a byte count or a value suffixed with k, m, g, or t (optionally followed by b)
    /// into bytes. Decimal values require a unit.
    /// </summary>
    public static bool TryParse(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.Length >= 2
            && ((normalized[0] == '"' && normalized[^1] == '"')
                || (normalized[0] == '\'' && normalized[^1] == '\'')))
        {
            normalized = normalized[1..^1].Trim();
        }

        var unitStart = 0;
        while (unitStart < normalized.Length
               && (char.IsDigit(normalized[unitStart]) || normalized[unitStart] == '.'))
        {
            unitStart++;
        }

        if (unitStart == 0)
        {
            return false;
        }

        var numericPart = normalized[..unitStart];
        var unit = normalized[unitStart..].Trim().ToLowerInvariant();
        if (!decimal.TryParse(
                numericPart,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var numericValue)
            || numericValue < 0)
        {
            return false;
        }

        long multiplier = unit switch
        {
            "t" or "tb" => 1024L * 1024L * 1024L * 1024L,
            "g" or "gb" => 1024L * 1024L * 1024L,
            "m" or "mb" => 1024L * 1024L,
            "k" or "kb" => 1024L,
            "" or "b" => 1L,
            _ => 0L
        };

        if (multiplier == 0 || (multiplier == 1 && numericValue != decimal.Truncate(numericValue)))
        {
            return false;
        }

        try
        {
            var byteValue = numericValue * multiplier;
            if (byteValue > long.MaxValue || (numericValue > 0 && byteValue < 1))
            {
                return false;
            }

            bytes = (long)byteValue;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
