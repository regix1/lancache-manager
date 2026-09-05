using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

public sealed class PersistentPrefillEditSessionGateTests
{
    [Fact]
    public async Task CleanupTombstone_WaitsForAcceptedStart_AndRejectsLateStart()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var start = gate.BeginStart("edit-session-a", "start-a");
        var editAction = gate.BeginEditAction(
            "edit-session-a",
            "login-a",
            PersistentPrefillEditActionKind.Login,
            "session-a");

        Assert.True(start.IsOwner);
        Assert.True(editAction.Accepted);

        var cleanup = gate.BeginCleanup("edit-session-a", "cleanup-a");
        Assert.True(cleanup.IsOwner);
        _ = Assert.Single(cleanup.PendingStarts);
        _ = Assert.Single(cleanup.PendingEditActions);

        var lateStart = gate.BeginStart("edit-session-a", "start-late");
        var lateEditAction = gate.BeginEditAction(
            "edit-session-a",
            "selection-late",
            PersistentPrefillEditActionKind.Selection,
            "session-a");
        Assert.False(lateStart.Accepted);
        Assert.False(lateEditAction.Accepted);

        gate.CompleteStart(
            "edit-session-a",
            "start-a",
            new PersistentPrefillEditSessionStartRecord("start-a", "session-a", CreatedByEditSession: true));
        editAction.Complete(PersistentPrefillEditActionOutcome.Succeeded);

        var completed = await cleanup.PendingStarts[0];
        await cleanup.PendingEditActions[0];
        Assert.Equal("session-a", completed.SessionId);
        Assert.True(gate.CanStopStartedSession("edit-session-a", completed));
    }

    [Fact]
    public async Task FailedLaterEditSessionStart_ReleasesTheEarlierOwnedSessionForCleanup()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var first = gate.BeginStart("edit-session-a", "start-a");
        gate.CompleteStart(
            "edit-session-a",
            "start-a",
            new PersistentPrefillEditSessionStartRecord("start-a", "session-a", CreatedByEditSession: true));
        var firstRecord = await first.Completion;

        _ = gate.BeginStart("edit-session-b", "start-b");
        Assert.False(gate.CanStopStartedSession("edit-session-a", firstRecord));
        Assert.True(gate.HasPendingLaterStart("edit-session-a", firstRecord));

        gate.FailStart("edit-session-b", "start-b", new InvalidOperationException("start failed"));

        Assert.False(gate.HasPendingLaterStart("edit-session-a", firstRecord));
        Assert.True(gate.CanStopStartedSession("edit-session-a", firstRecord));
    }

    [Fact]
    public async Task LaterEditSessionStart_ProtectsSharedSessionFromDelayedCleanup()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var first = gate.BeginStart("edit-session-a", "start-a");
        gate.CompleteStart(
            "edit-session-a",
            "start-a",
            new PersistentPrefillEditSessionStartRecord("start-a", "session-a", CreatedByEditSession: true));
        await first.Completion;

        var second = gate.BeginStart("edit-session-b", "start-b");

        var cleanup = gate.BeginCleanup("edit-session-a", "cleanup-a");
        var firstRecord = await cleanup.PendingStarts.Single();

        Assert.False(gate.CanStopStartedSession("edit-session-a", firstRecord));

        gate.CompleteStart(
            "edit-session-b",
            "start-b",
            new PersistentPrefillEditSessionStartRecord("start-b", "session-a", CreatedByEditSession: false));
        await second.Completion;
    }

    [Fact]
    public async Task CleanupRetry_JoinsInFlightCall_ThenCanRetryAfterFailure()
    {
        var gate = new PersistentPrefillEditSessionGate();

        var first = gate.BeginCleanup("edit-session-a", "cleanup-a");
        var joined = gate.BeginCleanup("edit-session-a", "cleanup-a");

        Assert.True(first.IsOwner);
        Assert.False(joined.IsOwner);
        Assert.Same(first.Completion, joined.Completion);

        var failure = new InvalidOperationException("transient");
        gate.FailCleanup("edit-session-a", "cleanup-a", failure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joined.Completion);

        var retry = gate.BeginCleanup("edit-session-a", "cleanup-a");
        Assert.True(retry.IsOwner);

        gate.CompleteCleanup("edit-session-a", "cleanup-a");
        await retry.Completion;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NewerSuccessfulEditAction_OwnsResource_AndFencesDelayedCleanup(
        int resourceKindValue)
    {
        var resourceKind = (PersistentPrefillEditResourceKind)resourceKindValue;
        var gate = new PersistentPrefillEditSessionGate();

        var first = gate.BeginEditAction(
            "edit-session-a",
            "action-a",
            ToActionKind(resourceKind),
            "session-shared");
        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            first.ConfirmEffect(resourceKind);
        }
        first.Complete(PersistentPrefillEditActionOutcome.Succeeded);

        var second = gate.BeginEditAction(
            "edit-session-b",
            "action-b",
            ToActionKind(resourceKind),
            "session-shared");
        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            second.ConfirmEffect(resourceKind);
        }
        second.Complete(PersistentPrefillEditActionOutcome.Succeeded);

        var cleanup = gate.BeginCleanup("edit-session-a", "cleanup-a");
        await Task.WhenAll(cleanup.PendingEditActions);

        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            Assert.Empty(gate.GetCompensableResources("edit-session-a", resourceKind));
            var owner = Assert.Single(gate.GetCompensableResources("edit-session-b", resourceKind));
            Assert.Equal("session-shared", owner.SessionId);
            Assert.Equal("action-b", owner.EditActionId);
        }
    }

    [Fact]
    public async Task ConflictOutcome_DoesNotOwnPrefillResource()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var action = gate.BeginEditAction(
            "edit-session-a",
            "prefill-a",
            PersistentPrefillEditActionKind.Prefill,
            "session-a");

        action.Complete(PersistentPrefillEditActionOutcome.Conflict);
        var completed = await action.Completion;

        Assert.Equal(PersistentPrefillEditActionOutcome.Conflict, completed.Outcome);
        Assert.Empty(completed.ConfirmedEffects);
        Assert.Empty(gate.GetCompensableResources(
            "edit-session-a",
            PersistentPrefillEditResourceKind.Prefill));
    }

    [Fact]
    public async Task PrefillConflict_RetainsItsConfirmedSelectionEffectOnly()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var action = gate.BeginEditAction(
            "edit-session-a",
            "prefill-a",
            PersistentPrefillEditActionKind.Prefill,
            "session-a");

        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            action.ConfirmEffect(PersistentPrefillEditResourceKind.Selection);
        }
        action.Complete(PersistentPrefillEditActionOutcome.Conflict);

        var completed = await action.Completion;
        Assert.Equal(
            [PersistentPrefillEditResourceKind.Selection],
            completed.ConfirmedEffects);
        Assert.Single(gate.GetCompensableResources(
            "edit-session-a",
            PersistentPrefillEditResourceKind.Selection));
        Assert.Empty(gate.GetCompensableResources(
            "edit-session-a",
            PersistentPrefillEditResourceKind.Prefill));
    }

    [Fact]
    public async Task ReusedLoginActionId_TracksEveryServerInvocation()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var login = gate.BeginEditAction(
            "edit-session-a",
            "login-a",
            PersistentPrefillEditActionKind.Login,
            "session-a");
        var credential = gate.BeginEditAction(
            "edit-session-a",
            "login-a",
            PersistentPrefillEditActionKind.Credential,
            "session-a");

        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            login.ConfirmEffect(PersistentPrefillEditResourceKind.Login);
        }
        login.Complete(PersistentPrefillEditActionOutcome.Succeeded);
        credential.Complete(PersistentPrefillEditActionOutcome.Conflict);

        var cleanup = gate.BeginCleanup("edit-session-a", "cleanup-a");
        Assert.Equal(2, cleanup.PendingEditActions.Count);
        await Task.WhenAll(cleanup.PendingEditActions);

        var loginRecord = await login.Completion;
        var credentialRecord = await credential.Completion;
        Assert.Equal(loginRecord.EditActionId, credentialRecord.EditActionId);
        Assert.True(loginRecord.Sequence < credentialRecord.Sequence);
        Assert.Single(gate.GetCompensableResources(
            "edit-session-a",
            PersistentPrefillEditResourceKind.Login));
    }

    [Fact]
    public async Task CompensationMutation_SerializesNewerAction()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var first = gate.BeginEditAction(
            "edit-session-a",
            "selection-a",
            PersistentPrefillEditActionKind.Selection,
            "session-shared");
        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            first.ConfirmEffect(PersistentPrefillEditResourceKind.Selection);
        }
        first.Complete(PersistentPrefillEditActionOutcome.Succeeded);

        var cleanupMutation = await gate.EnterMutationAsync(CancellationToken.None);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var second = gate.BeginEditAction(
            "edit-session-b",
            "selection-b",
            PersistentPrefillEditActionKind.Selection,
            "session-shared");
        var secondTask = Task.Run(async () =>
        {
            await using (await gate.EnterMutationAsync(CancellationToken.None))
            {
                secondEntered.TrySetResult();
                second.ConfirmEffect(PersistentPrefillEditResourceKind.Selection);
            }
            second.Complete(PersistentPrefillEditActionOutcome.Succeeded);
        });

        await Task.Yield();
        Assert.False(secondEntered.Task.IsCompleted);

        await cleanupMutation.DisposeAsync();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await secondTask;

        Assert.Empty(gate.GetCompensableResources(
            "edit-session-a",
            PersistentPrefillEditResourceKind.Selection));
        Assert.Single(gate.GetCompensableResources(
            "edit-session-b",
            PersistentPrefillEditResourceKind.Selection));
    }

    [Fact]
    public async Task TryEnterMutation_ReturnsImmediatelyWhileAnotherMutationOwnsTheGate()
    {
        var gate = new PersistentPrefillEditSessionGate();
        await using var first = await gate.EnterMutationAsync(CancellationToken.None);

        Assert.False(gate.TryEnterMutation(out var denied));
        Assert.Null(denied);

        await first.DisposeAsync();

        Assert.True(gate.TryEnterMutation(out var acquired));
        Assert.NotNull(acquired);
        await acquired!.DisposeAsync();
    }

    [Fact]
    public async Task LaterStartAdoption_RetainsContainerButNotEarlierActionOwnership()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var firstStart = gate.BeginStart("edit-session-a", "start-a");
        gate.CompleteStart(
            "edit-session-a",
            "start-a",
            new PersistentPrefillEditSessionStartRecord(
                "start-a",
                "session-shared",
                CreatedByEditSession: true));
        var firstRecord = await firstStart.Completion;

        var action = gate.BeginEditAction(
            "edit-session-a",
            "selection-a",
            PersistentPrefillEditActionKind.Selection,
            "session-shared");
        await using (await gate.EnterMutationAsync(CancellationToken.None))
        {
            action.ConfirmEffect(PersistentPrefillEditResourceKind.Selection);
        }
        action.Complete(PersistentPrefillEditActionOutcome.Succeeded);

        _ = gate.BeginStart("edit-session-b", "start-b");
        gate.CompleteStart(
            "edit-session-b",
            "start-b",
            new PersistentPrefillEditSessionStartRecord(
                "start-b",
                "session-shared",
                CreatedByEditSession: false));

        Assert.False(gate.CanStopStartedSession("edit-session-a", firstRecord));
        Assert.Single(gate.GetCompensableResources(
            "edit-session-a",
            PersistentPrefillEditResourceKind.Selection));
    }

    [Fact]
    public async Task CompletedCleanup_RetiresItsStarts_ButKeepsRejectingLateStarts()
    {
        var gate = new PersistentPrefillEditSessionGate();
        var first = gate.BeginStart("edit-session-a", "start-a");
        Assert.True(gate.HasPendingStart());
        gate.CompleteStart(
            "edit-session-a",
            "start-a",
            new PersistentPrefillEditSessionStartRecord("start-a", "session-a", CreatedByEditSession: true));
        var firstRecord = await first.Completion;

        var second = gate.BeginStart("edit-session-b", "start-b");
        gate.CompleteStart(
            "edit-session-b",
            "start-b",
            new PersistentPrefillEditSessionStartRecord("start-b", "session-b", CreatedByEditSession: true));
        await second.Completion;

        Assert.False(gate.CanStopStartedSession("edit-session-a", firstRecord));

        var cleanup = gate.BeginCleanup("edit-session-b", "cleanup-b");
        _ = await cleanup.PendingStarts.Single();
        gate.CompleteCleanup("edit-session-b", "cleanup-b");
        await cleanup.Completion;

        Assert.True(gate.CanStopStartedSession("edit-session-a", firstRecord));
        Assert.False(gate.BeginStart("edit-session-b", "start-late").Accepted);
        Assert.False(gate.BeginEditAction(
            "edit-session-b",
            "selection-late",
            PersistentPrefillEditActionKind.Selection,
            "session-b").Accepted);
    }

    private static PersistentPrefillEditActionKind ToActionKind(
        PersistentPrefillEditResourceKind resourceKind) =>
        resourceKind switch
        {
            PersistentPrefillEditResourceKind.Selection => PersistentPrefillEditActionKind.Selection,
            PersistentPrefillEditResourceKind.Login => PersistentPrefillEditActionKind.Login,
            PersistentPrefillEditResourceKind.Prefill => PersistentPrefillEditActionKind.Prefill,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceKind))
        };
}
