namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>Small shared parse helpers for lancache-style env var values, used by both
/// <see cref="CacheDomainsService"/> (NOFETCH) and <see cref="StatusCheckService"/> (DISABLE_&lt;SERVICE&gt;).</summary>
internal static class EnvValueParsing
{
    /// <summary>Case-insensitive true/false parse accepting the `true/1/yes` and `false/0/no`
    /// forms lancache env files use. Returns <c>null</c> for absent/unrecognized values so the
    /// caller can apply its own default.</summary>
    internal static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => null
        };
    }
}
