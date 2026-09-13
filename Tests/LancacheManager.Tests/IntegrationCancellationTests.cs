using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class IntegrationCancellationTests
{
    [Fact]
    public async Task ReporterPublishesAnImmutableHiddenActorPinBeforeTheStartedEvent()
    {
        using var fixture = new IntegrationFixture();
        var login = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        var checkedPin = false;
        ((Notifications)(object)notifications).OnSend = () =>
        {
            var operation = Assert.Single(tracker.GetActiveOperations());
            var values = Assert.IsType<Dictionary<string, object?>>(operation.Metadata);
            Assert.Same(login, values["integrationLogin"]);
            var json = JsonSerializer.Serialize(operation);
            Assert.DoesNotContain(login.AccountId!.Value.ToString(), json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("integrationLogin", json, StringComparison.Ordinal);
            checkedPin = true;
        };
        await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Steam,
            true, CancellationToken.None, NullLogger.Instance);
        await reporter.StartAsync(login: login);
        ((Notifications)(object)notifications).OnSend = null;
        var cancellation = new OperationCancellationService(tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<OperationCancellationService>.Instance);
        Assert.Throws<ForbiddenException>(() => cancellation.Cancel(reporter.OperationId, fixture.Other));
        await Assert.ThrowsAsync<ForbiddenException>(() => cancellation.ForceKillAsync(reporter.OperationId, fixture.Other));
        Assert.Throws<ForbiddenException>(() => cancellation.Cancel(reporter.OperationId,
            fixture.Owner with { SessionId = Guid.NewGuid() }));
        Assert.False(reporter.Token.IsCancellationRequested);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(reporter.OperationId)!.Status);
        Assert.True(checkedPin);
        Assert.Equal(OperationCancelResult.Requested, cancellation.Cancel(reporter.OperationId, fixture.Owner));
        Assert.True(reporter.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffAfterAuthorizationCannotRedirectCancellationToAnotherActor(bool forceKill)
    {
        using var fixture = new IntegrationFixture();
        var login = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        var tracker = NewTracker();
        using var originCancel = new CancellationTokenSource();
        using var successorCancel = new CancellationTokenSource();
        var origin = tracker.RegisterOperation(OperationType.DepotMapping, "Sign-in", originCancel,
            new Dictionary<string, object?> { ["integrationLogin"] = login });
        var successor = tracker.RegisterOperation(OperationType.DepotMapping, "Sign-in", successorCancel,
            new Dictionary<string, object?> { ["integrationLogin"] = login with { AccountId = fixture.Other.AccountId } });
        var proxy = DispatchProxy.Create<IUnifiedOperationTracker, HandoffTracker>();
        var forwarding = (HandoffTracker)(object)proxy;
        forwarding.Tracker = tracker;
        forwarding.Origin = origin;
        forwarding.Successor = successor;
        var cancellation = new OperationCancellationService(proxy,
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<OperationCancellationService>.Instance);
        if (forceKill) Assert.True(await cancellation.ForceKillAsync(origin, fixture.Owner));
        else Assert.Equal(OperationCancelResult.Requested, cancellation.Cancel(origin, fixture.Owner));
        Assert.True(forwarding.Recorded);
        Assert.True(originCancel.IsCancellationRequested);
        Assert.False(successorCancel.IsCancellationRequested);
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(successor)!.Status);

        // The former follow-at-dispatch behavior reaches the new target in the same interleaving.
        tracker.CancelOperation(origin);
        Assert.True(successorCancel.IsCancellationRequested);
    }

    private static UnifiedOperationTracker NewTracker() => new(
        new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);

    private class Notifications : DispatchProxy
    {
        public Action? OnSend { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            OnSend?.Invoke();
            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    private class HandoffTracker : DispatchProxy
    {
        public UnifiedOperationTracker Tracker { get; set; } = null!;
        public Guid Origin { get; set; }
        public Guid Successor { get; set; }
        public bool Recorded { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(Tracker, args);
            if (!Recorded && targetMethod.Name == nameof(IUnifiedOperationTracker.GetOperation))
            {
                Tracker.RecordHandoff(Origin, Successor);
                Recorded = true;
            }
            return result;
        }
    }
}
