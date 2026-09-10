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
