using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the scheduled-prefill follow-ups: (1) a partial per-service failure must report the run
/// as unsuccessful in <c>ScheduledPrefillCompleted</c> (via the pure <see cref="ScheduledPrefillRunGates.EvaluateRunOutcome"/>
/// helper the orchestrator now delegates to); (2) the DI-boot smoke test proving the
/// auth-orchestrator rip-out left the container able to activate <see cref="ScheduledPrefillService"/>
/// without the deleted scheduled-prefill auth-orchestrator dependency; and (3) a benign, user-initiated
/// cancellation of a scheduled run is swallowed INSIDE <see cref="ScheduledPrefillService"/> (logged at
/// Information and completed as cancelled) so it never surfaces to the shared
/// <c>ConfigurableScheduledService</c> loop as a hard "error in scheduled work", and the recurring
/// schedule keeps ticking afterward.
/// </summary>
public class ScheduledPrefillServiceTests
{
    // ---- Criterion 5: partial per-service failure must report success:false ----

    [Fact]
    public void EvaluateRunOutcome_ReportsSuccess_WhenServicesAttemptedAndNoneFailed()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesAttempted: 3, anyServiceFailed: false);

        Assert.True(outcome.Success);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsFailure_WhenAServiceFailed_EvenIfOthersAttempted()
    {
        // A service threw (per-service catch) or returned false (skipped / failed to engage) during
        // an otherwise-progressing run — the run as a whole must not claim full success.
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesAttempted: 2, anyServiceFailed: true);

        Assert.False(outcome.Success);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    [Fact]
    public void EvaluateRunOutcome_ReportsFailure_WhenNoServiceWasAttempted()
    {
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(servicesAttempted: 0, anyServiceFailed: false);

        Assert.False(outcome.Success);
        Assert.Equal("All enabled services were skipped", outcome.Error);
    }

    // ---- Criterion 3: DI-boot smoke test for the auth-orchestrator rip-out ----
    // After removing the dead auth-orchestrator dependency from the ScheduledPrefillService
    // constructor and from Program.cs DI, the container must still build and the hosted service must
    // activate WITHOUT that (now deleted) dependency. ValidateOnBuild proves the constructor's
    // call-site graph resolves with no missing dependency (a missing one throws here); the explicit
    // resolve then runs the real constructor. A plain unit test would not catch a DI-startup crash.

    [Fact]
    public void ServiceProvider_BuildsAndActivatesScheduledPrefillService_WithoutAuthService()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStateService>(CreateNullStateService());
        services.AddSingleton<ScheduledPrefillService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var resolved = provider.GetRequiredService<ScheduledPrefillService>();

        Assert.NotNull(resolved);
    }

    // ---- Benign cancellation is handled in ScheduledPrefillService, not the shared base loop ----
    // Regression guard: a user cancel of a running scheduled prefill must be swallowed inside
    // ExecuteWorkAsync (Information log + operation completed as cancelled) and must NOT propagate as an
    // OperationCanceledException to ConfigurableScheduledService.ExecuteAsync, whose generic catch would
    // mis-log the benign cancel as a hard "error in scheduled work" and where an over-broad OCE catch
    // would instead silently swallow genuinely-unrelated internal timeouts.

    [Fact]
    public async Task ExecuteWorkAsync_BenignCancellation_SwallowsException_AndCompletesOperationAsCancelled()
    {
        var harness = CreateCancellingHarness();
        using var provider = harness.Provider;

        var executeWork = typeof(ScheduledPrefillService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // The base scheduling loop calls ExecuteWorkAsync once per tick, so invoking it twice in a row
        // and getting a normal return each time is the method-level guarantee that a cancel (surfaced
        // here by the fake tracker cancelling the adopted CTS) is handled locally and never escapes to
        // the shared loop, which would otherwise log it as "error in scheduled work".
        var thrown = await Record.ExceptionAsync(async () =>
        {
            await (Task)executeWork.Invoke(harness.Service, new object[] { CancellationToken.None })!;
            await (Task)executeWork.Invoke(harness.Service, new object[] { CancellationToken.None })!;
        });

        harness.Service.Dispose();

        Assert.Null(thrown);
        Assert.Equal(2, harness.Tracker.RegisterCount);
        // Cleanup preserved: each run still completes its tracked operation, marked as cancelled.
        Assert.Equal(2, harness.Tracker.CompleteCount);
        Assert.False(harness.Tracker.LastCompleteSuccess);
        Assert.DoesNotContain(harness.Logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("cancelled"));
    }

    [Fact]
    public async Task SchedulingLoop_ContinuesTicking_AfterBenignCancellation()
    {
        var harness = CreateCancellingHarness();
        using var provider = harness.Provider;
        var service = harness.Service;

        await service.StartAsync(CancellationToken.None);
        try
        {
            // DefaultRunOnStartup is false, so the loop skips its first iteration and then sleeps on the
            // 1-minute poll cadence. Nudge it (TriggerImmediateRun also flags a bypass so every enabled
            // service is due) until it has executed work at least twice — proving a benign cancel on one
            // tick does not tear down the recurring schedule.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (harness.Tracker.RegisterCount < 2 && DateTime.UtcNow < deadline)
            {
                service.TriggerImmediateRun();
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        Assert.True(
            harness.Tracker.RegisterCount >= 2,
            $"Scheduling loop should keep running work after a benign cancel; it ran {harness.Tracker.RegisterCount} time(s).");
        Assert.DoesNotContain(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("error in scheduled work"));
        Assert.Contains(
            harness.Logger.Entries,
            entry => entry.Level == LogLevel.Information && entry.Message.Contains("cancelled"));
    }

    private static CancellingHarness CreateCancellingHarness()
    {
        var trackerProxy = DispatchProxy.Create<IUnifiedOperationTracker, CancellingTrackerProxy>();
        var tracker = (CancellingTrackerProxy)trackerProxy;
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, PrefillConfigStateServiceProxy>();

        var services = new ServiceCollection();
        services.AddSingleton((IUnifiedOperationTracker)trackerProxy);
        services.AddSingleton(notifications);
        var provider = services.BuildServiceProvider();

        var logger = new CapturingLogger();
        var service = new ScheduledPrefillService(
            logger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            stateService);

        return new CancellingHarness(service, logger, tracker, provider);
    }

    private sealed record CancellingHarness(
        ScheduledPrefillService Service,
        CapturingLogger Logger,
        CancellingTrackerProxy Tracker,
        ServiceProvider Provider);

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<ScheduledPrefillService>
    {
        private readonly object _sync = new();
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_sync) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    // Reproduces the user/tracker cancel path (OperationsController -> tracker.CancelOperation ->
    // cts.Cancel): cancelling the adopted CTS the instant the run registers makes runToken fire, which
    // ScheduledPrefillService.ExecuteWorkAsync must treat as a benign, already-handled cancellation.
    // Not sealed: DispatchProxy.Create derives the concrete proxy type from this class.
    private class CancellingTrackerProxy : DispatchProxy
    {
        private int _registerCount;
        private int _completeCount;

        public int RegisterCount => Volatile.Read(ref _registerCount);
        public int CompleteCount => Volatile.Read(ref _completeCount);
        public bool? LastCompleteSuccess { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                    Interlocked.Increment(ref _registerCount);
                    // args[2] is the CancellationTokenSource the run hands over (RegisterOperation's
                    // third parameter). Cancelling it here stands in for the user pressing Cancel.
                    (args?[2] as CancellationTokenSource)?.Cancel();
                    return Guid.NewGuid();
                case nameof(IUnifiedOperationTracker.CompleteOperation):
                    Interlocked.Increment(ref _completeCount);
                    // args[1] is the success flag; a benign cancel must complete with success:false.
                    LastCompleteSuccess = args?[1] as bool?;
                    return null;
                default:
                    return null;
            }
        }
    }

    // IStateService stub whose GetScheduledPrefillConfig returns a real default config (BattleNet + Riot
    // enabled), so ExecuteWorkAsync finds due services and reaches the operation-register/cancel path.
    // Every other member returns its type default (mirrors NullReturningProxy).
    private class PrefillConfigStateServiceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return ScheduledPrefillConfigFactory.CreateDefault();
            }

            var returnType = targetMethod?.ReturnType;

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType is not null && returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }

    private static IStateService CreateNullStateService()
        => (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

    /// <summary>
    /// Minimal <see cref="IStateService"/> stub. The <see cref="ScheduledPrefillService"/> constructor
    /// only reads <c>GetServiceInterval</c> / <c>GetServiceRunOnStartup</c> (both nullable) via
    /// <c>LoadStateOverrides</c>; returning null is the "no saved override" path. Every other member
    /// returns its type default — none are exercised during construction.
    /// </summary>
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Target method was null.");
            }

            var returnType = targetMethod.ReturnType;

            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            // Non-nullable value types need a concrete default; reference types and Nullable<T>
            // (e.g. double? / bool?) resolve to null.
            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
