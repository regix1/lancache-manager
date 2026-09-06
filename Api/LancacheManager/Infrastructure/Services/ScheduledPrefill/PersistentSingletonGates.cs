using Docker.DotNet.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// What the caller should do about an existing (or missing) persistent container for a service,
/// as decided by <see cref="PersistentSingletonGates.DecideExistingContainerAction"/>.
/// </summary>
public enum PersistentContainerAction
{
    /// <summary>No usable container exists - proceed straight to creating one.</summary>
    CreateFresh,

    /// <summary>A running, not-yet-adopted container exists - reconnect to it instead of creating.</summary>
    Adopt,

    /// <summary>Only stopped/exited containers exist - remove the target, then create fresh.</summary>
    Remove,

    /// <summary>
    /// Only stopped/exited containers exist AND the caller asked to recreate (FullPersistence mode on an
    /// enabled service): remove the target exactly as with <see cref="Remove"/>, then create a fresh
    /// container so the daemon self-authenticates from its named auth volume after the outage/crash that
    /// stopped it. Differs from <see cref="Remove"/> only in that the caller follows the removal with a
    /// create instead of leaving the service without a container.
    /// </summary>
    Recreate,

    /// <summary>A match is mid-removal - wait briefly and re-decide rather than acting now.</summary>
    RetryLater
}

/// <summary>
/// Immutable decision returned by <see cref="PersistentSingletonGates.DecideExistingContainerAction"/>.
/// <see cref="Target"/> is the single container the caller should act on for <see cref="Action"/>
/// (null for <see cref="PersistentContainerAction.CreateFresh"/> and
/// <see cref="PersistentContainerAction.RetryLater"/>). <see cref="ExtrasToRemove"/> holds every
/// OTHER matching container beyond <see cref="Target"/> - populated whenever more than one
/// persistent container was found for the service, so a single decision can also drive the
/// "adopt-newest, remove-the-rest" leaked-container migration.
/// </summary>
public readonly record struct PersistentContainerDecision(
    PersistentContainerAction Action,
    ContainerListResponse? Target,
    IReadOnlyList<ContainerListResponse> ExtrasToRemove)
{
    public static readonly IReadOnlyList<ContainerListResponse> NoExtras = Array.Empty<ContainerListResponse>();
}

/// <summary>
/// Pure gate logic for enforcing exactly one persistent daemon container per service - extracted
/// for unit testing. Docker/DB/logger-free: callers pass in the already-listed containers and the
/// already-known in-memory session state, and get back a decision to execute.
/// </summary>
public static class PersistentSingletonGates
{
    /// <summary>
    /// True when the service's existing persistent session has crashed (socket disconnect flips it
    /// to <see cref="DaemonSessionStatus.Error"/> without tearing down its container - leak M2) and
    /// must be replaced rather than left to shadow every future start attempt. An <c>Active</c>
    /// session is left alone (current behavior: reused). A non-persistent or already-terminated
    /// session is never a replace target.
    /// </summary>
    public static bool ShouldReplaceErroredSession(DaemonSession? existingPersistentSession)
        => existingPersistentSession is { IsPersistent: true, Status: DaemonSessionStatus.Error };

    /// <summary>
    /// Decides what to do about the set of Docker containers already matching this service's
    /// persistent name/label filter, given which container ids (if any) are already adopted
    /// in-memory. Used both before creating a persistent container (kills leak M1's 409/duplicate
    /// path) and during startup re-adoption (kills leak M1's invisible-zombie path), so the same
    /// "adopt the newest running one, remove every other match" rule applies in both places.
    /// <paramref name="recreateStoppedContainer"/> upgrades the stopped-container outcome from
    /// <see cref="PersistentContainerAction.Remove"/> to <see cref="PersistentContainerAction.Recreate"/>:
    /// startup re-adoption passes <c>true</c> for a FullPersistence, enabled service so a container that
    /// died while the manager was down is rebuilt (its named volume login survives), and <c>false</c>
    /// (the default, used by the pre-create path where a create already follows the removal) reaps it.
    /// It has no effect on the CreateFresh / Adopt / RetryLater outcomes.
    /// </summary>
    public static PersistentContainerDecision DecideExistingContainerAction(
        IReadOnlyList<ContainerListResponse> matchingContainers,
        IReadOnlySet<string> adoptedContainerIds,
        bool recreateStoppedContainer = false)
    {
        if (matchingContainers.Count == 0)
        {
            return new PersistentContainerDecision(PersistentContainerAction.CreateFresh, null, PersistentContainerDecision.NoExtras);
        }

        // A container mid-removal, or crash-looping under Docker's own restart policy, cannot be
        // meaningfully adopted or removed right now: "removing" is a transient Docker-side operation
        // already in flight, and "restarting" means the container may come back healthy on its own in
        // moments - force-removing it here would fight Docker's restart policy and could destroy a
        // container that was about to recover. Both wait-and-re-decide rather than act on stale state.
        // ("paused" is deliberately NOT included here: nothing in this codebase ever issues
        // `docker pause` against a persistent container, so a paused container only occurs via manual/
        // external intervention. It falls through to the "not running" branch below and is treated as
        // removable, same as any other non-running container - singleton enforcement should not be
        // silently blocked by an out-of-band pause.)
        if (matchingContainers.Any(c => string.Equals(c.State, "removing", StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(c.State, "restarting", StringComparison.OrdinalIgnoreCase)))
        {
            return new PersistentContainerDecision(PersistentContainerAction.RetryLater, null, PersistentContainerDecision.NoExtras);
        }

        var running = matchingContainers
            .Where(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Created)
            .ToList();

        if (running.Count > 0)
        {
            var newest = running[0];

            if (adoptedContainerIds.Contains(newest.ID))
            {
                // Defensive: the newest running match is already registered in-memory. The caller's
                // in-memory Active/Error checks should have short-circuited before this ever runs;
                // treat it as nothing-to-do for adoption - but any OTHER matching container is still a
                // leaked duplicate that must be reaped, same as the Adopt branch below. Both callers
                // process ExtrasToRemove before switching on Action, so this still gets cleaned up.
                var adoptedExtras = matchingContainers.Where(c => c.ID != newest.ID).ToList();
                return new PersistentContainerDecision(PersistentContainerAction.CreateFresh, null, adoptedExtras);
            }

            var extras = matchingContainers.Where(c => c.ID != newest.ID).ToList();
            return new PersistentContainerDecision(PersistentContainerAction.Adopt, newest, extras);
        }

        // Nothing running: every match is stopped/exited/created. Remove the newest match (and any
        // other leftovers). A FullPersistence, enabled service (recreateStoppedContainer:true)
        // additionally rebuilds a fresh container afterwards so its saved volume login comes back after
        // the outage/crash that stopped it; every other mode just reaps the dead container.
        var target = matchingContainers.OrderByDescending(c => c.Created).First();
        var rest = matchingContainers.Where(c => c.ID != target.ID).ToList();
        var stoppedAction = recreateStoppedContainer
            ? PersistentContainerAction.Recreate
            : PersistentContainerAction.Remove;
        return new PersistentContainerDecision(stoppedAction, target, rest);
    }

    /// <summary>
    /// Whether a FullPersistence service whose container vanished should be RECREATED at startup, given
    /// its effective <paramref name="effectiveMode"/>, whether it is <paramref name="serviceEnabled"/>,
    /// and the status of its most recent persistent DB session row
    /// (<paramref name="latestPersistentSessionStatus"/>; null = the service never had one). Recreate only
    /// when the service is FullPersistence + enabled AND the last life ended INVOLUNTARILY (a row exists
    /// and is not <see cref="PrefillSessionStatus.Terminated"/>): an explicit admin stop leaves a
    /// Terminated row and must never auto-resurrect a login, and a service that never ran (no row) is
    /// never fabricated. Pure so both the stopped-container and the zero-container retry paths in
    /// <c>PrefillDaemonServiceBase.ReadoptPersistentContainersAsync</c> share one tested rule.
    /// </summary>
    public static bool ShouldRecreatePersistentContainer(
        PersistenceMode effectiveMode,
        bool serviceEnabled,
        PrefillSessionStatus? latestPersistentSessionStatus)
        => effectiveMode == PersistenceMode.FullPersistence
           && serviceEnabled
           && latestPersistentSessionStatus is { } status
           && status != PrefillSessionStatus.Terminated;
}
