using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A waiting history upgrade card can be canceled at any point before its run starts, including while the request
/// that parked it has not yet kept the card's id. Either way the next merge request, an import's included, must reach
/// the queue again instead of being taken for the canceled one.
/// </summary>
public class CanceledWaitingHistoryUpgradeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnImportsMergeStillQueuesAfterTheWaitingUpgradeIsCanceledAsync(bool cancelBeforeTheIdIsKept)
    {
        await using var database = await TestDatabase.CreateAsync();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance,
            notifications);
        var checker = OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new CancelOnParkQueue(
            new OperationWaitlistService(tracker, checker, NullLogger<OperationWaitlistService>.Instance),
            tracker,
            cancelBeforeTheIdIsKept);
        using var service = new DownloadHistoryUpgradeService(
            database.Factory,
            notifications,
            tracker,
            queue,
            OperationConflictTestServices.Owner,
            Task.CompletedTask,
            NullLogger<DownloadHistoryUpgradeService>.Instance,
            checker);
        // A bulk run in progress, so the upgrade waits behind it for the whole test.
        tracker.RegisterOperation(OperationType.LogProcessing, "Log processing", new CancellationTokenSource());

        // The startup request parks the first card. In one order the cancel lands while that request is still
        // inside the queue call, in the other after the request has kept the card's id.
        await service.StartAsync(CancellationToken.None);
        await service.StartupRequest.WaitAsync(Timeout);
        var firstCard = await queue.FirstParked.WaitAsync(Timeout);
        if (!cancelBeforeTheIdIsKept)
        {
            await CancelAndWaitForEndAsync(tracker, firstCard);
        }
        Assert.Equal(OperationStatus.Canceled, tracker.GetOperation(firstCard)?.Status);

        // An import that brought rows in asks for its merge.
        queue.PassThrough();
        await service.RequestReplanAsync().WaitAsync(Timeout);

        var nextCard = tracker.GetWaitingOperations()
            .SingleOrDefault(operation => operation.Type == OperationType.DownloadHistoryUpgrade);
        try
        {
            Assert.True(
                nextCard is not null && nextCard.Id != firstCard,
                $"The import's merge request put no new history upgrade card in the queue after the waiting card was "
                + $"canceled {(cancelBeforeTheIdIsKept ? "before" : "after")} the request kept its id.");
        }
        finally
        {
            if (nextCard is not null)
            {
                await CancelAndWaitForEndAsync(tracker, nextCard.Id);
            }
        }
    }

    private static async Task CancelAndWaitForEndAsync(UnifiedOperationTracker tracker, Guid operationId)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnTerminal(OperationInfo operation)
        {
            if (operation.Id == operationId)
            {
                ended.TrySetResult();
            }
        }

        tracker.OperationTerminal += OnTerminal;
        try
        {
            Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(operationId));
            await ended.Task.WaitAsync(Timeout);
        }
        finally
        {
            tracker.OperationTerminal -= OnTerminal;
        }
    }

    /// <summary>
    /// The real queue, except that the first parked card can be canceled, and its end waited for, before the queue's
    /// answer goes back to the caller: the request has then not yet kept the card's id.
    /// </summary>
    private sealed class CancelOnParkQueue(
        IOperationWaitlist queue,
        UnifiedOperationTracker tracker,
        bool cancelBeforeAnswer) : IOperationWaitlist
    {
        private readonly TaskCompletionSource<Guid> _firstParked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _passThrough;

        public Task<Guid> FirstParked => _firstParked.Task;

        public void PassThrough() => _passThrough = true;

        public async Task<OperationStartResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            OperationRequestKey requestKey,
            bool reportRefusal = false,
            RunNotice? notice = null,
            string? target = null)
        {
            var outcome = await queue.EnqueueAsync(
                type, scope, displayName, start, ct, requestKey, reportRefusal, notice, target);
            if (_passThrough || outcome.Status != OperationStartStatus.Waiting)
            {
                return outcome;
            }

            if (cancelBeforeAnswer)
            {
                await CancelAndWaitForEndAsync(tracker, outcome.OperationId);
            }
            _firstParked.TrySetResult(outcome.OperationId);
            return outcome;
        }

        public Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type, ConflictScope scope, Func<Task<Guid?>> start, CancellationToken ct, OperationRequestKey requestKey)
            => queue.StartUnlessBusyAsync(type, scope, start, ct, requestKey);
    }
}
