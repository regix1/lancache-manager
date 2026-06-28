namespace LancacheManager.Controllers;

/// <summary>
/// Shared validation for guest prefill configuration endpoints.
/// </summary>
public static class GuestPrefillValidation
{
    private const string DurationHoursError = "Duration must be 1, 2, or 3 hours";

    /// <summary>
    /// Validates guest prefill duration hours (allowed range: 1–3 inclusive).
    /// </summary>
    public static bool TryValidateDurationHours(int hours, out string error)
    {
        if (hours is < 1 or > 3)
        {
            error = DurationHoursError;
            return false;
        }

        error = string.Empty;
        return true;
    }
}
