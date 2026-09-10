namespace LancacheManager.Controllers;

/// <summary>
/// What a scheduled scan mode is judged against: the facts about this install that survive at rest,
/// with no Steam session open. These are the same facts the scan mode dropdown reads in the browser,
/// so the two gates answer from the same evidence.
/// </summary>
internal readonly record struct DepotScanModeAvailability(
    bool SetupCompleted,
    bool RebuildRunning,
    int DepotMappingsFound,
    bool WebApiAvailable);

/// <summary>
/// Refusal body for a scheduled scan mode that cannot run with the depot data and credentials on
/// hand. <see cref="StageKey"/> is the i18n key the client renders; <see cref="Error"/> is the
/// English fallback for a client that does not localize.
/// </summary>
public class ScanModeUnavailableResponse
{
    public string StageKey { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
