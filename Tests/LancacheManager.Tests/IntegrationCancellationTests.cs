using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
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
            new RunNotice(NotificationMode.All, RunTrigger.Manual), CancellationToken.None, NullLogger.Instance);
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

    [Theory]
    [InlineData(NotificationMode.All)]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Silent)]
    [InlineData(NotificationMode.Hidden)]
    public async Task EpicSignInRunRegistersAFreshManualNoticeInTheScheduleMode(NotificationMode mode)
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        using var service = NewEpicService(fixture, http, services, tracker);
        service.SetNotificationMode(mode);
        var start = await service.GetAuthorizationUrl(fixture.Owner);

        await service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);

        var run = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal("completed", run.Status);
        var notice = tracker.GetOperation(run.OperationId)!.Notice!;
        Assert.Equal(mode, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
    }

    [Fact]
    public async Task EpicRefreshRunsRegisterTheNoticeTheyWereAdmittedWith()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        using var service = NewEpicService(fixture, http, services, tracker);

        // Signed out, a scheduled tick still resolves the stored patterns under its admitted notice.
        var signedOut = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);
        await (Task)typeof(EpicMappingService).GetMethod("RunWithoutSignInAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [CancellationToken.None, signedOut])!;
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        await service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);
        var scheduled = new RunNotice(NotificationMode.Manual, RunTrigger.Scheduled);
        var refreshed = await RefreshAsync(service, tracker,
            () => service.TryStartRefresh(CancellationToken.None, RunTrigger.Scheduled, scheduled));
        // With no notice the refresh builds a manual one in the service mode.
        var direct = await RefreshAsync(service, tracker, () => service.TryStartRefresh());

        var notices = tracker.GetRuns().Runs.ToDictionary(run => run.OperationId, run => tracker.GetOperation(run.OperationId)!.Notice!);
        Assert.Contains(signedOut, notices.Values);
        Assert.Same(scheduled, notices[refreshed]);
        Assert.Equal(NotificationMode.Manual, notices[direct].Mode);
        Assert.Equal(RunTrigger.Manual, notices[direct].Trigger);
    }

    // Starts one refresh, waits for it to end, and returns the id of the run it registered.
    private static async Task<Guid> RefreshAsync(EpicMappingService service, UnifiedOperationTracker tracker, Func<bool> start)
    {
        var before = tracker.GetRuns().Runs.Select(run => run.OperationId).ToHashSet();
        Assert.True(start());
        var run = (Task)typeof(EpicMappingService).GetField("_currentRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        return Assert.Single(tracker.GetRuns().Runs, listed => !before.Contains(listed.OperationId)).OperationId;
    }

    private static EpicMappingService NewEpicService(
        IntegrationFixture fixture, HttpClient http, ServiceProvider services, UnifiedOperationTracker tracker) => new(
        NullLogger<EpicMappingService>.Instance,
        new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance), fixture.Epic,
        DispatchProxy.Create<ISignalRNotificationService, Notifications>(), null!, tracker,
        services.GetRequiredService<IServiceScopeFactory>(), DispatchProxy.Create<IStateService, NullReturningProxy>());

    [Fact]
    public void SteamSignInNeverStartsItsReporter()
    {
        // The reporter is only the sign-in's cancellation handle. Started and never completed, it
        // would fail a successful sign-in at dispose, and the sign-in has no run of its own.
        var source = File.ReadAllText(Path.Combine(EndpointAuthorizationHost.FindRepositoryRoot(),
            "Api", "LancacheManager", "Core", "Services", "SteamKit2", "SteamKit2Service.Authentication.cs"));

        Assert.Contains("reporter = CreateDepotMappingReporter(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".StartAsync(", source, StringComparison.Ordinal);
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

    // The code exchange answers tokens, and every catalog read answers an empty list.
    private sealed class EpicSignInHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Post
                    ? """{"access_token":"access","refresh_token":"refresh","expires_at":"2099-01-01T00:00:00Z","refresh_expires":28800,"expires_in":3600,"displayName":"owner","account_id":"epic"}"""
                    : "[]", Encoding.UTF8, "application/json")
            });
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
