using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Scheduled run endings that hold on every platform: a run that ends with a warning keeps its amber row, and a
/// person's cancel landing during a Stop's logout ends the run as stopped.
/// </summary>
public class ScheduledPrefillAnonymousRunPathTests
{
    [Fact]
    public async Task CompleteServiceRunAsync_RanWithAWarning_EndsAsAKeptAmberRow()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var serviceConfig = ScheduledPrefillConfigFactory.CreateDefault().GetSchedulesInRunOrder()[0];
        var state = new ScheduledPrefillServiceRunState(
            serviceConfig.ServiceId,
            serviceConfig.ScheduleId,
            serviceConfig.ScheduleName,
            new RunNotice(NotificationMode.All, RunTrigger.Scheduled));
        var operationId = tracker.RegisterOperation(
            OperationType.ScheduledPrefill, "Scheduled Prefill", new CancellationTokenSource(), state);
        var serviceRun = new ScheduledPrefillServiceRun(
            serviceConfig, operationId, operationId.ToString(), "run-1", state, CancellationToken.None);
        state.Warning = new RunWarning(
            "signalr.scheduledPrefill.failedApps",
            new Dictionary<string, object?> { ["failed"] = 1, ["total"] = 3 });
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();

        await (Task)typeof(ScheduledPrefillService)
            .GetMethod("CompleteServiceRunAsync", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [serviceRun, tracker, notifications, ScheduledPrefillServiceRunResult.Ran, null])!;

        var row = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == operationId);
        Assert.Equal("completed", row.Status);
        Assert.Equal("signalr.scheduledPrefill.failedApps", Assert.Single(row.Warnings).StageKey);
        Assert.True(row.Retained);
    }

    /// <summary>
    /// A person's Cancel landing while Stop is still logging the container out ends a newer daemon's
    /// scheduled run as stopped once Stop ends it, and sends the daemon no cancel.
    /// </summary>
    [Fact]
    public async Task RunAndStampServiceAsync_NewerDaemonCancelDuringStop_EndsAsStopped()
    {
        var fixture = await RunFixture.CreateAsync(persistent: true);
        var client = DispatchProxy.Create<IDaemonClient, HeldLogoutRunClient>();
        var held = (HeldLogoutRunClient)(object)client;
        fixture.Session.Client = client;
        fixture.Session.Capabilities = RunClient.Capabilities(held.InstanceId);
        var operationId = Guid.NewGuid();
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        using var person = new CancellationTokenSource();
        var serviceRun = MakeServiceRun(new ScheduledPrefillServiceConfigDto
        {
            ServiceId = PrefillPlatform.Steam,
            ScheduleId = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam),
            ScheduleName = "Default",
            Enabled = true,
            NotificationMode = NotificationMode.Silent,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = new List<string> { "10" },
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        }) with { OperationId = operationId, OperationIdString = operationId.ToString(), Token = person.Token };

        var run = RunAndStampAsync(PrefillPlatform.Steam, fixture.Daemon, serviceRun, config);
        var startDeadline = DateTime.UtcNow.AddSeconds(5);
        while (fixture.Daemon.GetRun(fixture.Session.Id, operationId) is null && DateTime.UtcNow < startDeadline)
        {
            await Task.Delay(20);
        }
        Assert.NotNull(fixture.Daemon.GetRun(fixture.Session.Id, operationId));
        var stop = fixture.Daemon.TerminateSessionAsync(fixture.Session.Id, "Persistent session stopped");
        await held.LogoutStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await person.CancelAsync();
        // The watch sends the cancel every 250 ms, so a second inside the hold covers several of them.
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));
        held.Logout.TrySetResult(new LogoutOutcome(Success: true, RequiresLogin: false));
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        var (result, stageKey) = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, result);
        Assert.Equal("signalr.scheduledPrefill.stopped", stageKey);
        Assert.Empty(held.Cancelled);
    }

    /// <summary>
    /// Drives the real <c>RunAndStampServiceAsync</c>, which turns a thrown failure or a cancel into the
    /// run's end, and returns the run result with the stage key its closing event carries.
    /// </summary>
    private static async Task<(ScheduledPrefillServiceRunResult Result, string? StageKey)> RunAndStampAsync(
        PrefillPlatform platform,
        PrefillDaemonServiceBase daemon,
        ScheduledPrefillServiceRun serviceRun,
        ScheduledPrefillConfigDto config)
    {
        using var daemons = BuildProviderWithDaemon(platform, daemon);
        using var schedulerServices = new ServiceCollection().BuildServiceProvider();
        var state = DispatchProxy.Create<IStateService, RecordingNotificationsProxy>();
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, RecordingNotificationsProxy>();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        using var scheduler = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            schedulerServices.GetRequiredService<IServiceScopeFactory>(), state);
        var method = typeof(ScheduledPrefillService).GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<ScheduledPrefillServiceRunResult>)method.Invoke(scheduler,
            new object?[] { serviceRun, tracker, daemons, notifications, config, serviceRun.Token, true, null })!;
        var completed = Assert.Single(((RecordingNotificationsProxy)(object)notifications).Calls,
            c => c.Args.Length > 1 && c.Args[0] as string == SignalREvents.ScheduledPrefillCompleted);
        return (result, (string?)completed.Args[1]!.GetType().GetProperty("stageKey")!.GetValue(completed.Args[1]));
    }

    /// <summary>
    /// The one platform's slice of a run that <c>RunServiceAsync</c> now takes instead of a bare
    /// operation id: a real tracked run also carries its own cancellation token, but nothing in these
    /// tests cancels, so <see cref="CancellationToken.None"/> stands in for it.
    /// </summary>
    private static ScheduledPrefillServiceRun MakeServiceRun(ScheduledPrefillServiceConfigDto serviceConfig)
        => new(
            serviceConfig,
            Guid.Empty,
            "op-1",
            "run-1",
            new ScheduledPrefillServiceRunState(
                serviceConfig.ServiceId,
                serviceConfig.ScheduleId,
                serviceConfig.ScheduleName,
                new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled)),
            CancellationToken.None);

    private static ServiceProvider BuildProviderWithDaemon(PrefillPlatform platform, PrefillDaemonServiceBase daemon)
    {
        var services = new ServiceCollection();
        switch (platform)
        {
            case PrefillPlatform.Steam:
                services.AddSingleton((SteamDaemonService)daemon);
                break;
            case PrefillPlatform.BattleNet:
                services.AddSingleton((BattleNetDaemonService)daemon);
                break;
            case PrefillPlatform.Riot:
                services.AddSingleton((RiotDaemonService)daemon);
                break;
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Records the <c>stage</c> field of every <c>NotifyAllAsync</c> payload
    /// (<see cref="ScheduledPrefillService"/>'s <c>ReportProgressAsync</c> is the only caller that
    /// shapes payloads this way), so the test can assert the run never emitted "needs-login" and did
    /// reach "completed". Every other member returns its type default (mirrors NullReturningProxy).
    /// </summary>
    // Not sealed: DispatchProxy.Create derives a runtime subclass, which the compiler can only
    // allow casting an interface-typed reference back to when the class isn't sealed (mirrors
    // ScheduledPrefillServiceTests.CancellingTrackerProxy).
    private class RecordingNotificationsProxy : DispatchProxy
    {
        private readonly object _sync = new();
        public ScheduledPrefillConfigDto Config { get; set; } = ScheduledPrefillConfigFactory.CreateDefault();
        public Action<string, object>? Notification { get; set; }
        public List<(string Method, object?[] Args)> Calls { get; } = new();
        public List<string> Stages { get; } = new();

        /// <summary>Payloads that carried a visibility field (any name ending in "Notification").</summary>
        public int VisibilityFieldCount { get; private set; }

        /// <summary>
        /// The <c>stageKey</c> alongside each recorded stage. Nullable entries are the point: the
        /// property is always present on the payload, so a caller that stops passing a key records a
        /// null here rather than dropping out of the list.
        /// </summary>
        public List<string?> StageKeys { get; } = new();

        public List<double?> PercentCompleteValues { get; } = new();
        public List<long?> BytesDownloaded { get; } = new();
        public List<long?> TotalBytes { get; } = new();

        /// <summary>
        /// The one sentence each recorded stage put on the card. It is what the user reads, so a
        /// terminal line that names the daemon's own reason can only be asserted from here.
        /// </summary>
        public List<string> Messages { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            lock (_sync)
            {
                if (targetMethod is not null)
                {
                    Calls.Add((targetMethod.Name, args ?? []));
                }
                if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
                {
                    return Config;
                }

                if (targetMethod?.Name == nameof(IUnifiedOperationTracker.RegisterOperation)) return Guid.NewGuid();

                if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args is { Length: >= 2 })
                {
                    Notification?.Invoke((string)args[0]!, args[1]!);
                    var stageProperty = args[1]?.GetType().GetProperty("stage");
                    if (stageProperty?.GetValue(args[1]) is string stage)
                    {
                        Stages.Add(stage);
                    }

                    var messageProperty = args[1]?.GetType().GetProperty("message");
                    if (messageProperty?.GetValue(args[1]) is string message)
                    {
                        Messages.Add(message);
                    }

                    var stageKeyProperty = args[1]?.GetType().GetProperty("stageKey");
                    if (stageKeyProperty is not null)
                    {
                        StageKeys.Add(stageKeyProperty.GetValue(args[1]) as string);
                    }

                    var percentProperty = args[1]?.GetType().GetProperty("percentComplete");
                    if (percentProperty is not null)
                    {
                        PercentCompleteValues.Add((double?)percentProperty.GetValue(args[1]));
                    }

                    var bytesProperty = args[1]?.GetType().GetProperty("bytesDownloaded");
                    if (bytesProperty is not null)
                    {
                        BytesDownloaded.Add((long?)bytesProperty.GetValue(args[1]));
                    }

                    var totalBytesProperty = args[1]?.GetType().GetProperty("totalBytes");
                    if (totalBytesProperty is not null)
                    {
                        TotalBytes.Add((long?)totalBytesProperty.GetValue(args[1]));
                    }

                    if (args[1]?.GetType().GetProperties()
                        .Any(property => property.Name.EndsWith("Notification", StringComparison.Ordinal)) == true)
                    {
                        VisibilityFieldCount++;
                    }
                }

                return DefaultReturnValue(targetMethod);
            }
        }
    }

    /// <summary>
    /// Default-return logic for the recording proxy above: Task methods get a completed task,
    /// non-nullable value types get their default instance, everything else (including void) is null.
    /// </summary>
    private static object? DefaultReturnValue(MethodInfo? targetMethod)
    {
        var returnType = targetMethod?.ReturnType;

        if (returnType is null || returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }

    /// <summary>
    /// A newer daemon whose logout waits until the test answers it, holding Stop's teardown open.
    /// Not sealed, because <see cref="DispatchProxy"/> derives from it.
    /// </summary>
    private class HeldLogoutRunClient : RunClient
    {
        public TaskCompletionSource LogoutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<LogoutOutcome> Logout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IDaemonClient.LogoutWithReasonAsync))
                return base.Invoke(targetMethod, args);
            LogoutStarted.TrySetResult();
            return Logout.Task;
        }
    }
}
