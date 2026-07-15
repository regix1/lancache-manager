using Docker.DotNet.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public class PersistentSingletonGateTests
{
    private static readonly DateTime NowUtc = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<string> NoneAdopted = new HashSet<string>();

    private static DaemonSession MakeSession(bool isPersistent, DaemonSessionStatus status)
    {
        return new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = status,
            IsPersistent = isPersistent,
            CreatedAt = NowUtc.AddDays(-1),
            ExpiresAt = NowUtc.AddDays(1)
        };
    }

    private static ContainerListResponse MakeContainer(string id, string state, DateTime created)
        => new()
        {
            ID = id,
            State = state,
            Created = created,
            Names = new List<string> { $"/steam-daemon-persistent" }
        };

    // === ShouldReplaceErroredSession ===

    [Fact]
    public void ShouldReplaceErroredSession_ReturnsTrue_ForPersistentErrorSession()
    {
        var session = MakeSession(isPersistent: true, status: DaemonSessionStatus.Error);
        Assert.True(PersistentSingletonGates.ShouldReplaceErroredSession(session));
    }

    [Fact]
    public void ShouldReplaceErroredSession_ReturnsFalse_ForPersistentActiveSession()
    {
        // An Active persistent session is reused, not replaced - current behavior.
        var session = MakeSession(isPersistent: true, status: DaemonSessionStatus.Active);
        Assert.False(PersistentSingletonGates.ShouldReplaceErroredSession(session));
    }

    [Fact]
    public void ShouldReplaceErroredSession_ReturnsFalse_ForNonPersistentErrorSession()
    {
        // Guest sessions in Error state are reaped by the normal expiry/termination path, not this gate.
        var session = MakeSession(isPersistent: false, status: DaemonSessionStatus.Error);
        Assert.False(PersistentSingletonGates.ShouldReplaceErroredSession(session));
    }

    [Fact]
    public void ShouldReplaceErroredSession_ReturnsFalse_ForNull()
    {
        Assert.False(PersistentSingletonGates.ShouldReplaceErroredSession(null));
    }

    // === DecideExistingContainerAction ===

    [Fact]
    public void DecideExistingContainerAction_ReturnsCreateFresh_WhenNoContainersMatch()
    {
        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            Array.Empty<ContainerListResponse>(), NoneAdopted);

        Assert.Equal(PersistentContainerAction.CreateFresh, decision.Action);
        Assert.Null(decision.Target);
        Assert.Empty(decision.ExtrasToRemove);
    }

    [Fact]
    public void DecideExistingContainerAction_ReturnsAdopt_ForRunningUnadoptedContainer()
    {
        var container = MakeContainer("running-1", "running", NowUtc.AddHours(-1));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { container }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Adopt, decision.Action);
        Assert.Equal("running-1", decision.Target?.ID);
        Assert.Empty(decision.ExtrasToRemove);
    }

    [Fact]
    public void DecideExistingContainerAction_ReturnsCreateFresh_WhenNewestRunningIsAlreadyAdopted()
    {
        var container = MakeContainer("running-1", "running", NowUtc.AddHours(-1));
        var adopted = new HashSet<string> { "running-1" };

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { container }, adopted);

        Assert.Equal(PersistentContainerAction.CreateFresh, decision.Action);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void DecideExistingContainerAction_NewestAlreadyAdopted_StillReturnsOtherMatchesAsExtras()
    {
        // Regression: previously returned NoExtras here, silently leaving leaked sibling containers
        // running forever once the newest match was adopted (C2 finding).
        var adoptedRunning = MakeContainer("adopted-running", "running", NowUtc.AddHours(-1));
        var extraRunning = MakeContainer("extra-running", "running", NowUtc.AddHours(-2));
        var extraStopped = MakeContainer("extra-stopped", "exited", NowUtc.AddHours(-3));
        var adopted = new HashSet<string> { "adopted-running" };

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { adoptedRunning, extraRunning, extraStopped }, adopted);

        Assert.Equal(PersistentContainerAction.CreateFresh, decision.Action);
        Assert.Null(decision.Target);
        Assert.Equal(2, decision.ExtrasToRemove.Count);
        Assert.Contains(decision.ExtrasToRemove, c => c.ID == "extra-running");
        Assert.Contains(decision.ExtrasToRemove, c => c.ID == "extra-stopped");
        Assert.DoesNotContain(decision.ExtrasToRemove, c => c.ID == "adopted-running");
    }

    [Fact]
    public void DecideExistingContainerAction_ReturnsRemove_ForStoppedContainer()
    {
        var container = MakeContainer("stopped-1", "exited", NowUtc.AddHours(-2));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { container }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Remove, decision.Action);
        Assert.Equal("stopped-1", decision.Target?.ID);
        Assert.Empty(decision.ExtrasToRemove);
    }

    [Fact]
    public void DecideExistingContainerAction_ReturnsRetryLater_WhenAMatchIsMidRemoval()
    {
        var removing = MakeContainer("removing-1", "removing", NowUtc);
        var running = MakeContainer("running-1", "running", NowUtc.AddHours(-1));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { removing, running }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.RetryLater, decision.Action);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void DecideExistingContainerAction_ReturnsRetryLater_WhenAMatchIsRestarting()
    {
        // A crash-looping container may recover on its own under Docker's restart policy; it must not
        // be force-removed as if it were simply stopped (Cursor finding #4).
        var restarting = MakeContainer("restarting-1", "restarting", NowUtc);

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { restarting }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.RetryLater, decision.Action);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void DecideExistingContainerAction_ReturnsRemove_ForPausedContainer()
    {
        // By design: nothing in this codebase ever pauses a persistent container, so a paused
        // container only results from manual/external intervention. It is treated as "not running"
        // (same bucket as stopped/exited) rather than given RetryLater's wait-and-recover treatment.
        var paused = MakeContainer("paused-1", "paused", NowUtc.AddHours(-1));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { paused }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Remove, decision.Action);
        Assert.Equal("paused-1", decision.Target?.ID);
    }

    [Fact]
    public void DecideExistingContainerAction_MultipleRunningContainers_AdoptsNewestRemovesRest()
    {
        var older = MakeContainer("older", "running", NowUtc.AddHours(-3));
        var newer = MakeContainer("newer", "running", NowUtc.AddHours(-1));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { older, newer }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Adopt, decision.Action);
        Assert.Equal("newer", decision.Target?.ID);
        Assert.Single(decision.ExtrasToRemove);
        Assert.Equal("older", decision.ExtrasToRemove[0].ID);
    }

    [Fact]
    public void DecideExistingContainerAction_RunningPlusStopped_AdoptsRunningRemovesStopped()
    {
        var stopped = MakeContainer("stopped-1", "exited", NowUtc.AddHours(-5));
        var running = MakeContainer("running-1", "running", NowUtc.AddHours(-1));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { stopped, running }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Adopt, decision.Action);
        Assert.Equal("running-1", decision.Target?.ID);
        Assert.Single(decision.ExtrasToRemove);
        Assert.Equal("stopped-1", decision.ExtrasToRemove[0].ID);
    }

    [Fact]
    public void DecideExistingContainerAction_MultipleStoppedContainers_RemovesNewestAndTheRest()
    {
        var older = MakeContainer("older-stopped", "exited", NowUtc.AddHours(-5));
        var newer = MakeContainer("newer-stopped", "exited", NowUtc.AddHours(-2));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { older, newer }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Remove, decision.Action);
        Assert.Equal("newer-stopped", decision.Target?.ID);
        Assert.Single(decision.ExtrasToRemove);
        Assert.Equal("older-stopped", decision.ExtrasToRemove[0].ID);
    }

    // === DecideExistingContainerAction with recreateStoppedContainer (FullPersistence, mode 3) ===

    [Fact]
    public void DecideExistingContainerAction_ReturnsRecreate_ForStoppedContainer_WhenRecreateRequested()
    {
        // FullPersistence, enabled service: a container that died while the manager was down must be
        // rebuilt, not just reaped. The Target is still the dead container the caller removes first.
        var container = MakeContainer("stopped-1", "exited", NowUtc.AddHours(-2));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { container }, NoneAdopted, recreateStoppedContainer: true);

        Assert.Equal(PersistentContainerAction.Recreate, decision.Action);
        Assert.Equal("stopped-1", decision.Target?.ID);
        Assert.Empty(decision.ExtrasToRemove);
    }

    [Fact]
    public void DecideExistingContainerAction_MultipleStopped_RecreateRequested_RecreatesNewestRemovesRest()
    {
        var older = MakeContainer("older-stopped", "exited", NowUtc.AddHours(-5));
        var newer = MakeContainer("newer-stopped", "exited", NowUtc.AddHours(-2));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { older, newer }, NoneAdopted, recreateStoppedContainer: true);

        Assert.Equal(PersistentContainerAction.Recreate, decision.Action);
        Assert.Equal("newer-stopped", decision.Target?.ID);
        Assert.Single(decision.ExtrasToRemove);
        Assert.Equal("older-stopped", decision.ExtrasToRemove[0].ID);
    }

    [Fact]
    public void DecideExistingContainerAction_RecreateRequested_RunningContainer_StillAdopts()
    {
        // The recreate flag only upgrades the stopped-container outcome; a running container is still
        // adopted (never rebuilt) in every mode.
        var container = MakeContainer("running-1", "running", NowUtc.AddHours(-1));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { container }, NoneAdopted, recreateStoppedContainer: true);

        Assert.Equal(PersistentContainerAction.Adopt, decision.Action);
        Assert.Equal("running-1", decision.Target?.ID);
    }

    [Fact]
    public void DecideExistingContainerAction_RecreateRequested_NoContainers_StillCreateFresh_NeverFabricates()
    {
        // No container ever existed: even FullPersistence must not fabricate one - a never-started
        // service stays untouched at startup.
        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            Array.Empty<ContainerListResponse>(), NoneAdopted, recreateStoppedContainer: true);

        Assert.Equal(PersistentContainerAction.CreateFresh, decision.Action);
        Assert.Null(decision.Target);
    }

    [Fact]
    public void DecideExistingContainerAction_StoppedContainer_DefaultsToRemove_WhenRecreateNotRequested()
    {
        // Regression guard: the default (KillOnRestart / KeepAcrossRestart, or the pre-create path)
        // keeps today's stopped -> Remove outcome unchanged.
        var container = MakeContainer("stopped-1", "exited", NowUtc.AddHours(-2));

        var decision = PersistentSingletonGates.DecideExistingContainerAction(
            new List<ContainerListResponse> { container }, NoneAdopted);

        Assert.Equal(PersistentContainerAction.Remove, decision.Action);
        Assert.Equal("stopped-1", decision.Target?.ID);
    }

    // === ShouldRecreatePersistentContainer: FullPersistence + enabled + last life ended INVOLUNTARILY ===

    [Theory]
    [InlineData(PrefillSessionStatus.Orphaned, true)]
    [InlineData(PrefillSessionStatus.Active, true)]
    [InlineData(PrefillSessionStatus.Cancelled, true)]
    [InlineData(PrefillSessionStatus.Terminated, false)] // explicit admin stop -> never auto-resurrect
    public void ShouldRecreatePersistentContainer_FullPersistenceEnabled_TracksPriorRowStatus(
        PrefillSessionStatus priorStatus, bool expected)
    {
        Assert.Equal(expected, PersistentSingletonGates.ShouldRecreatePersistentContainer(
            PersistenceMode.FullPersistence, serviceEnabled: true, priorStatus));
    }

    [Fact]
    public void ShouldRecreatePersistentContainer_ReturnsFalse_WhenNoPriorRow()
    {
        // Never fabricate a container for a service that never had one.
        Assert.False(PersistentSingletonGates.ShouldRecreatePersistentContainer(
            PersistenceMode.FullPersistence, serviceEnabled: true, latestPersistentSessionStatus: null));
    }

    [Fact]
    public void ShouldRecreatePersistentContainer_ReturnsFalse_WhenServiceDisabled()
    {
        Assert.False(PersistentSingletonGates.ShouldRecreatePersistentContainer(
            PersistenceMode.FullPersistence, serviceEnabled: false, PrefillSessionStatus.Orphaned));
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    public void ShouldRecreatePersistentContainer_ReturnsFalse_ForNonFullPersistenceMode(PersistenceMode mode)
    {
        Assert.False(PersistentSingletonGates.ShouldRecreatePersistentContainer(
            mode, serviceEnabled: true, PrefillSessionStatus.Orphaned));
    }
}
