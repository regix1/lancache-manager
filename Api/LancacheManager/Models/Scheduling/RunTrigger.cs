namespace LancacheManager.Models;

/// <summary>
/// Why a scheduled service's work is currently executing: the recurring interval elapsed,
/// the app just started, a user pressed Run Now, or a user pressed Run All. Exposed by the
/// scheduling base classes as <c>CurrentRunTrigger</c> so a service can distinguish a manual run
/// from an automatic one when deciding whether to surface notifications. Run All starts runs on a
/// person's request but counts as automatic for the notification mode. Game detection saves the
/// trigger as a number, so a new member is only ever appended.
/// </summary>
public enum RunTrigger
{
    Scheduled,
    Startup,
    Manual,
    RunAll
}
