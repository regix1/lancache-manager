namespace LancacheManager.Models;

/// <summary>
/// Why a scheduled service's work is currently executing: the recurring interval elapsed,
/// the app just started, or a user pressed Run Now. Exposed by the scheduling base classes as
/// <c>CurrentRunTrigger</c> so a service can distinguish a manual run from an automatic one when
/// deciding whether to surface notifications.
/// </summary>
public enum RunTrigger
{
    Scheduled,
    Startup,
    Manual
}
