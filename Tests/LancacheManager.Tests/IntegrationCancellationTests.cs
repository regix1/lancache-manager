using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            new ProcessManager(NullLogger<ProcessManager>.Instance), OperationConflictTestServices.Owner,
            NullLogger<OperationCancellationService>.Instance);
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

    [Fact]
    public async Task AMappingRunIsNamedByItsCardTitleAsync()
    {
        // The queue prints a running operation's name as the blocker in another card's waiting line.
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.All, RunTrigger.Manual), CancellationToken.None, NullLogger.Instance);

        await reporter.StartAsync();

        Assert.Equal("Epic Game Mapping", tracker.GetOperation(reporter.OperationId)!.Name);
    }

    [Fact]
    public async Task AReporterWhoseRunWasForceStoppedStillReadsItsCancelAsync()
    {
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.All, RunTrigger.Manual), CancellationToken.None, NullLogger.Instance);
        await reporter.StartAsync();
        var cancellation = new OperationCancellationService(tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), OperationConflictTestServices.Owner,
            NullLogger<OperationCancellationService>.Instance);

        Assert.True(await cancellation.ForceKillAsync(reporter.OperationId));

        // Completing the run disposed the source the token came from.
        Assert.True(reporter.Token.IsCancellationRequested);
        Assert.Equal(OperationStatus.Cancelled, tracker.GetOperation(reporter.OperationId)!.Status);
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
            new ProcessManager(NullLogger<ProcessManager>.Instance), OperationConflictTestServices.Owner,
            NullLogger<OperationCancellationService>.Instance);
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
        Assert.Empty(run.Warnings);
        var notice = tracker.GetOperation(run.OperationId)!.Notice!;
        Assert.Equal(mode, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
    }

    [Fact]
    public async Task AnEpicSignInWhoseWindowRunsOutBeforeTheAccountIsSavedEndsRedAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler { HangCatalog = true });
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        using var service = NewEpicService(fixture, http, services, tracker);
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        var login = fixture.Epic.ContinueIntegrationLogin(fixture.Owner, start.AttemptId);
        fixture.Epic.SetIntegrationLoginExpiry(login, DateTime.UtcNow.AddSeconds(2));

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId)
                .WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Equal("errors.integration.attemptExpired", refused.StageKey);
        var run = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal("failed", run.Status);
        Assert.Equal("errors.integration.attemptExpired", run.Error);
    }

    [Fact]
    public async Task AnEpicSignInWhoseWindowRunsOutWhileItsAccountIsSavedEndsSignedInAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var state = DispatchProxy.Create<IStateService, SlowLastCollectionState>();
        using var service = NewEpicService(fixture, http, services, tracker, state);
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        var login = fixture.Epic.ContinueIntegrationLogin(fixture.Owner, start.AttemptId);
        var expiry = DateTime.UtcNow.AddSeconds(2);
        fixture.Epic.SetIntegrationLoginExpiry(login, expiry);
        // The state write inside the account save holds until the window's timer has fired.
        ((SlowLastCollectionState)(object)state).Until = expiry.AddMilliseconds(300);

        await service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId)
            .WaitAsync(TimeSpan.FromSeconds(20));

        var run = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal("completed", run.Status);
        Assert.True(service.IsAuthenticated);
    }

    [Fact]
    public async Task AnEpicLogoutThatMeetsAnEndedSignInsSourceAnswersAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        using var service = NewEpicService(fixture, http, services, tracker);
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        await service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);
        // The state the sign-in's cleanup leaves between disposing its source and clearing the field.
        var disposed = new CancellationTokenSource();
        disposed.Dispose();
        typeof(EpicMappingService).GetField("_currentRefreshCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, disposed);

        await service.LogoutAsync(fixture.Owner).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(service.IsAuthenticated);
    }

    [Fact]
    public async Task AnEpicSignInWhoseCdnStepFailedEndsAmberAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler { FailCdn = true });
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        using var service = NewEpicService(fixture, http, services, tracker);
        var start = await service.GetAuthorizationUrl(fixture.Owner);

        await service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);

        var run = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal("completed", run.Status);
        var warning = Assert.Single(run.Warnings);
        Assert.Equal("common.notifications.warnings.epicStepsFailed", warning.StageKey);
        // Only the CDN step failed; the downloads step reads an empty database.
        Assert.Equal(1, warning.Context["count"]);
    }

    [Fact]
    public async Task XOnTheEpicSignInCardDuringItsCdnStepStopsTheSignInAsync()
    {
        using var fixture = new IntegrationFixture();
        var tracker = NewTracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pressed = false;
        // The card's X during the CDN step is the tracker cancel the cancel endpoint makes. The slow Epic server answers
        // only when the test releases it, unless the request's own token is canceled first.
        using var http = new HttpClient(new EpicSignInHandler
        {
            AtCdnRead = async cancellationToken =>
            {
                pressed = tracker.CancelOperation(Assert.Single(tracker.GetRuns().Runs).OperationId)
                    == OperationCancelResult.Requested;
                await release.Task.WaitAsync(cancellationToken);
            }
        });
        using var services = new ServiceCollection().BuildServiceProvider();
        using var service = NewEpicService(fixture, http, services, tracker);
        var start = await service.GetAuthorizationUrl(fixture.Owner);

        var signIn = service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);
        // A sign-in the X did not reach is still held by the server; releasing it lets the test read how it ended.
        if (await Task.WhenAny(signIn, Task.Delay(TimeSpan.FromSeconds(10))) != signIn) release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signIn.WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(pressed);
        Assert.Equal("cancelled", Assert.Single(tracker.GetRuns().Runs).Status);
        Assert.False(service.IsAuthenticated);
        var ending = service.GetAuthStatus(fixture.Owner, start.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(start.AttemptId, ending.AttemptId);
        Assert.Equal(OperationStatus.Cancelled, ending.Status);
        Assert.Equal("errors.integration.attemptExpired", ending.StageKey);
    }

    [Fact]
    public async Task XOnTheEpicSignInCardAfterItsAccountIsSavedLeavesTheSignInDoneAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var state = DispatchProxy.Create<IStateService, SlowLastCollectionState>();
        using var service = NewEpicService(fixture, http, services, tracker, state);
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        // The card's X lands inside the account save's state write, so the next step after the save sees the cancel.
        ((SlowLastCollectionState)(object)state).Until = DateTime.UtcNow;
        ((SlowLastCollectionState)(object)state).OnWrite = () =>
            tracker.CancelOperation(Assert.Single(tracker.GetRuns().Runs).OperationId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId)
                .WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.True(service.IsAuthenticated);
        var ending = service.GetAuthStatus(fixture.Owner, start.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(OperationStatus.Completed, ending.Status);
        Assert.Equal("signalr.epicMapping.completed", ending.StageKey);
    }

    [Fact]
    public async Task ForceStopWhileTheEpicStartedSendIsHeldKeepsTheCancellationEndingAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        var recorder = (Notifications)(object)notifications;
        recorder.HeldEvent = SignalREvents.EpicMappingStarted;
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var cancellation = new OperationCancellationService(tracker,
            new ProcessManager(NullLogger<ProcessManager>.Instance), OperationConflictTestServices.Owner,
            NullLogger<OperationCancellationService>.Instance);
        var start = await service.GetAuthorizationUrl(fixture.Owner);

        var submit = service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);
        try
        {
            // The run is registered before its Started event goes out, so a second browser can already press X.
            await recorder.HeldEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await cancellation.ForceKillAsync(Assert.Single(tracker.GetActiveOperations()).Id, fixture.Owner));
        }
        finally
        {
            recorder.HeldRelease.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("cancelled", Assert.Single(tracker.GetRuns().Runs).Status);
        var ending = service.GetAuthStatus(fixture.Owner, start.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(OperationStatus.Cancelled, ending.Status);
        Assert.Equal("errors.integration.attemptExpired", ending.StageKey);
    }

    [Fact]
    public async Task AnEpicSubmitCanceledWhileItWaitsForTheSessionLockEndsCanceledAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        var sessionLock = (SemaphoreSlim)typeof(EpicMappingService)
            .GetField("_sessionLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        await sessionLock.WaitAsync();

        var submit = service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);
        await service.CancelRefreshAsync(fixture.Owner, start.AttemptId);
        sessionLock.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submit.WaitAsync(TimeSpan.FromSeconds(10)));
        var ending = service.GetAuthStatus(fixture.Owner, start.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(OperationStatus.Cancelled, ending.Status);
        Assert.Equal("errors.integration.attemptExpired", ending.StageKey);
        Assert.Contains(((Notifications)(object)notifications).Sent, sent =>
            sent.EventName == SignalREvents.IntegrationLoginEnded
            && sent.Payload is SignalRNotifications.IntegrationLoginEnded ended && ended.AttemptId == start.AttemptId);
    }

    [Fact]
    public async Task AnEpicSignInThatSavedTheAccountRecordsItsEndingAndSendsItAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var start = await service.GetAuthorizationUrl(fixture.Owner);

        await service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId);

        var ending = service.GetAuthStatus(fixture.Owner, start.AttemptId).LoginEnding;
        Assert.NotNull(ending);
        Assert.Equal(OperationStatus.Completed, ending.Status);
        Assert.Equal("signalr.epicMapping.completed", ending.StageKey);
        Assert.Contains(((Notifications)(object)notifications).Sent, sent =>
            sent.EventName == SignalREvents.IntegrationLoginEnded
            && sent.Payload is SignalRNotifications.IntegrationLoginEnded ended && ended.AttemptId == start.AttemptId);
    }

    /// <summary>
    /// An Epic attempt whose code never reached the server has no submit to end it. The service announces the
    /// attempt's window end itself, so a waiting dialog reads once and finds the attempt gone.
    /// </summary>
    [Fact]
    public async Task AnEpicSignInNobodySentACodeForAnnouncesItsWindowEndAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        var recorder = (Notifications)(object)notifications;
        recorder.HeldEvent = SignalREvents.IntegrationLoginEnded;
        fixture.Epic.IntegrationLoginWindow = TimeSpan.FromSeconds(1);
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);

        var start = await service.GetAuthorizationUrl(fixture.Owner);
        try
        {
            await recorder.HeldEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            recorder.HeldRelease.TrySetResult();
        }

        Assert.Contains(recorder.Sent, sent =>
            sent.EventName == SignalREvents.IntegrationLoginEnded
            && sent.Payload is SignalRNotifications.IntegrationLoginEnded ended && ended.AttemptId == start.AttemptId);
    }

    /// <summary>
    /// The window-end delay runs on a monotonic timer while the attempt's window is judged by the wall clock. When the
    /// host clock stepped back during the delay, the push must wait until the wall clock is past the window's end, or
    /// the read it triggers finds the attempt still open and nothing announces the end again. The test moves the open
    /// attempt's expiry 3 seconds later, which is what a clock that stepped back 3 seconds looks like to the service.
    /// </summary>
    [Fact]
    public async Task AnEpicWindowEndIsAnnouncedOnlyOnceTheWallClockAgreesAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        var recorder = (Notifications)(object)notifications;
        recorder.HeldEvent = SignalREvents.IntegrationLoginEnded;
        fixture.Epic.IntegrationLoginWindow = TimeSpan.FromSeconds(1);
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var start = await service.GetAuthorizationUrl(fixture.Owner);
        var live = (IntegrationLogin)typeof(EpicMappingService)
            .GetField("_loginAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var movedExpiry = live.ExpiresAtUtc + TimeSpan.FromSeconds(3);
        var expiryField = typeof(IntegrationLogin)
            .GetField("<ExpiresAtUtc>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(expiryField);
        expiryField.SetValue(live, movedExpiry);

        DateTime pushedAt;
        try
        {
            await recorder.HeldEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            pushedAt = DateTime.UtcNow;
        }
        finally
        {
            recorder.HeldRelease.TrySetResult();
        }

        Assert.True(pushedAt > movedExpiry);
        Assert.Contains(recorder.Sent, sent =>
            sent.EventName == SignalREvents.IntegrationLoginEnded
            && sent.Payload is SignalRNotifications.IntegrationLoginEnded ended && ended.AttemptId == start.AttemptId);
    }

    /// <summary>
    /// Task.Delay refuses more than about 49.7 days, which is what a host clock stepped back that far looks like to the
    /// window-end loop. Copies the window that <c>GetAuthorizationUrl</c> hands to the loop, with a window past the limit.
    /// </summary>
    [Fact]
    public async Task AnEpicWindowFarBeyondTheTimerLimitIsWaitedInStepsAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        fixture.Epic.IntegrationLoginWindow = TimeSpan.FromDays(60);
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        await service.GetAuthorizationUrl(fixture.Owner);
        var live = (IntegrationLogin)typeof(EpicMappingService)
            .GetField("_loginAttempt", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

        var announce = (Task)typeof(EpicMappingService)
            .GetMethod("AnnounceLoginWindowEndAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [live])!;

        Assert.False(announce.IsFaulted);
        Assert.False(announce.IsCompleted);
    }

    [Fact]
    public async Task AnEpicRefreshCancelStopsTheRefreshsOwnCardAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var refresh = new CancellationTokenSource();
        await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual), refresh.Token, NullLogger.Instance);
        await reporter.StartAsync();
        typeof(EpicMappingService).GetField("_isProcessingInt", flags)!.SetValue(service, 1);
        typeof(EpicMappingService).GetField("_currentRefreshCts", flags)!.SetValue(service, refresh);
        typeof(EpicMappingService).GetField("_currentMappingReporter", flags)!.SetValue(service, reporter);

        Assert.True(await service.CancelRefreshAsync());

        Assert.True(reporter.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ACancelBeforeTheRefreshRegistersStopsItAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var refresh = new CancellationTokenSource();
        // The state a cancel sees between the refresh publishing its reporter and the reporter registering its card.
        await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual), refresh.Token, NullLogger.Instance);
        typeof(EpicMappingService).GetField("_isProcessingInt", flags)!.SetValue(service, 1);
        typeof(EpicMappingService).GetField("_currentRefreshCts", flags)!.SetValue(service, refresh);
        typeof(EpicMappingService).GetField("_currentMappingReporter", flags)!.SetValue(service, reporter);

        Assert.True(await service.CancelRefreshAsync());

        Assert.True(reporter.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AnEpicRefreshCancelThatMeetsTheRefreshsEndAnswersAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        using var service = NewEpicService(fixture, http, services, tracker, notifications: notifications);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var refresh = new CancellationTokenSource();
        await using var reporter = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual), refresh.Token, NullLogger.Instance);
        await reporter.StartAsync();
        await reporter.CompleteAsync(success: true);
        // The state a cancel sees between the refresh's completion and its cleanup of these fields.
        typeof(EpicMappingService).GetField("_isProcessingInt", flags)!.SetValue(service, 1);
        typeof(EpicMappingService).GetField("_currentRefreshCts", flags)!.SetValue(service, refresh);
        typeof(EpicMappingService).GetField("_currentMappingReporter", flags)!.SetValue(service, reporter);

        Assert.False(await service.CancelRefreshAsync());
    }

    [Fact]
    public async Task AnEpicRefreshCancelThatMeetsTheNextRefreshLeavesItRunningAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler());
        using var services = new ServiceCollection().BuildServiceProvider();
        var logger = new RefreshEndingLogger();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        var tracker = NewTracker();
        using var service = new EpicMappingService(
            logger,
            new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance), fixture.Epic,
            notifications,
            new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"epic_cancel_{Guid.NewGuid():N}").Options), tracker,
            services.GetRequiredService<IServiceScopeFactory>(), DispatchProxy.Create<IStateService, NullReturningProxy>());
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var cts = typeof(EpicMappingService).GetField("_currentRefreshCts", flags)!;
        var reporterField = typeof(EpicMappingService).GetField("_currentMappingReporter", flags)!;
        using var refresh = new CancellationTokenSource();
        using var nextRefresh = new CancellationTokenSource();
        await using var current = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual), refresh.Token, NullLogger.Instance);
        await current.StartAsync();
        await using var next = new MappingOperationReporter(notifications, tracker, MappingOperations.Epic,
            new RunNotice(NotificationMode.Manual, RunTrigger.Manual), nextRefresh.Token, NullLogger.Instance);
        await next.StartAsync();
        typeof(EpicMappingService).GetField("_isProcessingInt", flags)!.SetValue(service, 1);
        cts.SetValue(service, refresh);
        reporterField.SetValue(service, current);
        // The refresh ends and the next one starts while the cancel logs, between its read of the pair and its cancel.
        logger.OnCancelling = () =>
        {
            cts.SetValue(service, nextRefresh);
            reporterField.SetValue(service, next);
        };

        Assert.True(await service.CancelRefreshAsync());

        Assert.True(current.Token.IsCancellationRequested);
        Assert.False(next.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task AnEpicSignInWhoseCatalogReadTimesOutEndsRedAsync()
    {
        using var fixture = new IntegrationFixture();
        using var http = new HttpClient(new EpicSignInHandler { HangCatalog = true }) { Timeout = TimeSpan.FromSeconds(1) };
        using var services = new ServiceCollection().BuildServiceProvider();
        var tracker = NewTracker();
        using var service = NewEpicService(fixture, http, services, tracker);
        var start = await service.GetAuthorizationUrl(fixture.Owner);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            service.OnAuthCodeReceivedAsync("code", caller: fixture.Owner, attemptId: start.AttemptId)
                .WaitAsync(TimeSpan.FromSeconds(20)));

        var run = Assert.Single(tracker.GetRuns().Runs);
        Assert.Equal("failed", run.Status);
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
        IntegrationFixture fixture, HttpClient http, ServiceProvider services, UnifiedOperationTracker tracker,
        IStateService? state = null, ISignalRNotificationService? notifications = null) => new(
        NullLogger<EpicMappingService>.Instance,
        new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance), fixture.Epic,
        notifications ?? DispatchProxy.Create<ISignalRNotificationService, Notifications>(),
        new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"epic_sign_in_{Guid.NewGuid():N}").Options), tracker,
        services.GetRequiredService<IServiceScopeFactory>(), state ?? DispatchProxy.Create<IStateService, NullReturningProxy>());

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
        public List<(string EventName, object? Payload)> Sent { get; } = [];
        // The send of this event does not finish until HeldRelease completes, as a congested client's write does.
        public string? HeldEvent { get; set; }
        public TaskCompletionSource HeldEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HeldRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            OnSend?.Invoke();
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                lock (Sent) Sent.Add(((string)args![0]!, args[1]));
                if ((string)args![0]! == HeldEvent)
                {
                    HeldEntered.TrySetResult();
                    return HeldRelease.Task;
                }
            }
            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    // The code exchange answers tokens, and every catalog read answers an empty list. With HangCatalog a
    // read never answers, so it ends only when the caller's token or the client's timeout cancels it.
    // With FailCdn the second launcher-assets read (the first is the catalog read, the second the CDN step's)
    // fails at the transport, as SocketsHttpHandler does when the server refuses the connection.
    // With AtCdnRead the CDN step's read runs that callback first, which can hold the read until the request's token is canceled.
    private sealed class EpicSignInHandler : HttpMessageHandler
    {
        private int _assetReads;

        public bool HangCatalog { get; init; }
        public bool FailCdn { get; init; }
        public Func<CancellationToken, Task>? AtCdnRead { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (HangCatalog && request.Method != HttpMethod.Post) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/launcher/api/public/assets/Windows", StringComparison.Ordinal)
                && Interlocked.Increment(ref _assetReads) == 2)
            {
                if (FailCdn) throw new HttpRequestException("Connection refused");
                if (AtCdnRead is not null) await AtCdnRead(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method == HttpMethod.Post
                    ? """{"access_token":"access","refresh_token":"refresh","expires_at":"2099-01-01T00:00:00Z","refresh_expires":28800,"expires_in":3600,"displayName":"owner","account_id":"epic"}"""
                    : "[]", Encoding.UTF8, "application/json")
            };
        }
    }

    // Runs a callback when the service logs that it is canceling the active refresh, which is after the cancel read the refresh's source.
    private sealed class RefreshEndingLogger : ILogger<EpicMappingService>
    {
        public Action? OnCancelling { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("Cancelling active Epic catalog refresh", StringComparison.Ordinal))
            {
                OnCancelling?.Invoke();
            }
        }
    }

    // A state write that takes as long as a slow disk: the real SetEpicMappingLastCollection writes state.json.
    private class SlowLastCollectionState : NullReturningProxy
    {
        public DateTime Until { get; set; }
        public Action? OnWrite { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IStateService.SetEpicMappingLastCollection))
            {
                OnWrite?.Invoke();
                while (DateTime.UtcNow <= Until) Thread.Sleep(25);
            }
            return base.Invoke(targetMethod, args);
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
