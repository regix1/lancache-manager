using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the ownership rule that keeps log processing from wedging "busy" forever. The service is a
/// singleton and the interactive path clears IsProcessing before its display delay, so a second run
/// can register its own operation while the first is still finishing. A run must therefore complete
/// the operation it registered rather than whatever the field currently holds, and must tear down
/// only the state it installed - while a run that set the busy flag and installed nothing still has
/// to clear that flag on the way out.
/// </summary>
public sealed class LogProcessingOperationOwnershipTests
{
    [Fact]
    public void OwnsOperationState_TrueWhenTheFieldStillHoldsThisRunsId()
    {
        var operationId = Guid.NewGuid();

        Assert.True(RustLogProcessorService.OwnsOperationState(operationId, operationId));
    }

    [Fact]
    public void OwnsOperationState_FalseWhenALaterRunInstalledItsOwnId()
    {
        // The interleaving this exists for: interactive run A is inside its display delay when live
        // tick B registers. A must leave B's id, cancellation source and busy flag alone.
        var interactiveId = Guid.NewGuid();
        var liveTickId = Guid.NewGuid();

        Assert.False(RustLogProcessorService.OwnsOperationState(liveTickId, interactiveId));
    }

    [Fact]
    public void OwnsOperationState_FalseWhenATerminalCleanupAlreadyClearedTheField()
    {
        // A force-kill ran the terminal cleanup, which already reset the busy flags and disposed the
        // cancellation source. Repeating that teardown would clear whatever came after it.
        Assert.False(RustLogProcessorService.OwnsOperationState(null, Guid.NewGuid()));
    }

    [Fact]
    public void OwnsOperationState_TrueWhenTheRunNeverRegisteredAnything()
    {
        // Both absent is the run that set IsProcessing and failed before registering. It owns the
        // flag it set, so it must still be allowed to clear it.
        Assert.True(RustLogProcessorService.OwnsOperationState(null, null));
    }

    [Fact]
    public async Task FailedRun_ClearsTheBusyFlagAndTheOperationIdAsync()
    {
        using var fixture = new ProcessorFixture();

        // The resolved rust executable does not exist, so the run fails after registering. Whichever
        // path it leaves by, it must not strand the busy flag: the conflict checker reads it, and a
        // stuck one blocks every other heavy operation until the process restarts.
        var started = await fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            silentMode: true);

        Assert.False(started);
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Null(fixture.Processor.CurrentOperationId);
    }

    [Fact]
    public async Task FailedRun_LeavesNoOperationRunningAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath, silentMode: true);

        var stillRunning = fixture.Tracker.GetActiveOperations()
            .Where(o => o.Type == OperationType.LogProcessing)
            .ToList();

        Assert.Empty(stillRunning);
    }

    [Fact]
    public async Task RegistrationFailure_DisposesTheUnadoptedCancellationSourceAsync()
    {
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, RegistrationFailureTracker>();
        var failedTracker = (RegistrationFailureTracker)(object)tracker;
        using var fixture = new ProcessorFixture(tracker);

        var started = await fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            silentMode: true);

        Assert.False(started);
        var source = Assert.IsType<CancellationTokenSource>(failedTracker.Source);
        Assert.Throws<ObjectDisposedException>(() => _ = source.Token);
    }

    [Fact]
    public void BatchTerminalCleanup_LeavesCancellationSourceDisposalToTheTracker()
    {
        using var fixture = new ProcessorFixture();
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sourceField = typeof(RustLogProcessorService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operationId = (Guid)begin.Invoke(fixture.Processor, null)!;
        var registeredSource = fixture.Tracker.GetOperation(operationId)!.CancellationTokenSource!;
        using var cleanupSource = new DisposeTrackingCancellationTokenSource();
        sourceField.SetValue(fixture.Processor, cleanupSource);

        fixture.Tracker.CompleteOperation(operationId, false, cancelled: true);

        Assert.Throws<ObjectDisposedException>(() => _ = registeredSource.Token);
        Assert.Equal(0, cleanupSource.DisposeCalls);
        Assert.Null(sourceField.GetValue(fixture.Processor));
    }

    [Fact]
    public void EarlierTerminal_UsesItsOwnMetricsAndLeavesTheNextRunRegistered()
    {
        using var fixture = new ProcessorFixture();
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = (Guid)begin.Invoke(fixture.Processor, null)!;
        var next = (Guid)begin.Invoke(fixture.Processor, null)!;
        var metricType = typeof(RustLogProcessorService).GetNestedType("LogProcessingTerminalMetrics", BindingFlags.NonPublic)!;
        var metrics = Activator.CreateInstance(metricType, [7L, 11L, 1.5, "first completed", "complete"])!;

        fixture.Tracker.CompleteOperation(first, true, onCompleting: operation => operation.Metadata = metrics);
        var losingPublication = false;
        fixture.Tracker.CompleteOperation(first, false, error: "late failure", onCompleting: _ => losingPublication = true);

        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(Assert.Single(fixture.Messages.Completions));
        Assert.Equal(first, complete.OperationId);
        Assert.Equal(7, complete.EntriesProcessed);
        Assert.Equal(11, complete.LinesProcessed);
        Assert.False(losingPublication);
        Assert.Equal(next, fixture.Processor.CurrentOperationId);
        Assert.True(fixture.Processor.IsProcessing);
        Assert.False(fixture.Tracker.GetOperation(next)!.Status.IsTerminal());
        fixture.Tracker.CompleteOperation(next, false, cancelled: true);
    }

    [Fact]
    public async Task ExternalCompletionBeforeWorkerFailure_PreservesTheNextRun()
    {
        using var fixture = new ProcessorFixture();
        fixture.Messages.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Messages.Resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = fixture.Processor.StartProcessingAsync(fixture.LogFilePath);
        await fixture.Messages.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var original = fixture.Processor.CurrentOperationId!.Value;
        var registeredSource = fixture.Tracker.GetOperation(original)!.CancellationTokenSource!;
        var sourceField = typeof(RustLogProcessorService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var cleanupSource = new DisposeTrackingCancellationTokenSource();
        sourceField.SetValue(fixture.Processor, cleanupSource);
        fixture.Tracker.CompleteOperation(original, false, error: "external failure");
        Assert.Throws<ObjectDisposedException>(() => _ = registeredSource.Token);
        Assert.Equal(0, cleanupSource.DisposeCalls);
        var begin = typeof(RustLogProcessorService).GetMethod("BeginOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var next = (Guid)begin.Invoke(fixture.Processor, null)!;
        fixture.Messages.Resume.TrySetResult();
        Assert.False(await task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(next, fixture.Processor.CurrentOperationId);
        var complete = Assert.IsType<SignalRNotifications.LogProcessingComplete>(Assert.Single(fixture.Messages.Completions));
        Assert.Equal(original, complete.OperationId);
        Assert.Equal("external failure", complete.Message);
        fixture.Tracker.CompleteOperation(next, false, cancelled: true);
    }

    public class CompletionMessages : DispatchProxy
    {
        public List<object> Completions { get; } = [];
        public TaskCompletionSource? Started { get; set; }
        public TaskCompletionSource? Resume { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if (args![1] is SignalRNotifications.LogProcessingComplete completion)
                    Completions.Add(completion);
                if (args[0] is string eventName && eventName == "LogProcessingStarted" && Started != null)
                {
                    Started.TrySetResult();
                    return Resume!.Task;
                }
                return Task.CompletedTask;
            }
            return targetMethod.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    public class RegistrationFailureTracker : DispatchProxy
    {
        public CancellationTokenSource? Source { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IUnifiedOperationTracker.RegisterOperation))
            {
                Source = (CancellationTokenSource)args![2]!;
                throw new InvalidOperationException("Registration failed");
            }

            throw new NotSupportedException(targetMethod.Name);
        }
    }

    private sealed class DisposeTrackingCancellationTokenSource : CancellationTokenSource
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ProcessorFixture : IDisposable
    {
        private readonly string _root;

        public ProcessorFixture(IUnifiedOperationTracker? tracker = null)
        {
            _root = Path.Combine(Path.GetTempPath(), $"log-processing-ownership-{Guid.NewGuid():N}");
            var logPath = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logPath);
            Directory.CreateDirectory(Path.Combine(_root, "cache"));

            // PathResolverProxy answers every string-returning member with <root>/<member name>, so
            // the operations directory has to exist under that name for the positions file write.
            Directory.CreateDirectory(Path.Combine(_root, "GetOperationsDirectory"));

            LogFilePath = Path.Combine(logPath, "access.log");
            File.WriteAllText(LogFilePath, string.Empty);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "default",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(_root, "cache"),
                    ["LanCache:DataSources:0:LogPath"] = logPath,
                    ["LanCache:DataSources:0:Enabled"] = "true"
                })
                .Build();

            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = _root;

            var notifications = DispatchProxy.Create<ISignalRNotificationService, CompletionMessages>();
            Messages = (CompletionMessages)(object)notifications;

            Tracker = tracker ?? new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);

            Processor = new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                pathResolver,
                notifications,
                CreateStateService(_root, configuration, pathResolver),
                serviceProvider: null!,
                new RustProcessHelper(
                    NullLogger<RustProcessHelper>.Instance,
                    new ProcessManager(NullLogger<ProcessManager>.Instance),
                    pathResolver,
                    Tracker),
                new DatasourceService(
                    configuration,
                    pathResolver,
                    NullLogger<DatasourceService>.Instance),
                Tracker);
        }

        public string LogFilePath { get; }
        public IUnifiedOperationTracker Tracker { get; }
        public RustLogProcessorService Processor { get; }
        public CompletionMessages Messages { get; }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        private static StateService CreateStateService(
            string root,
            IConfiguration configuration,
            IPathResolver pathResolver)
        {
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "dp-keys")));
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                configuration,
                pathResolver);
            var encryption = new SecureStateEncryptionService(
                dataProtection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance,
                pathResolver,
                encryption);
            var state = new StateService(
                NullLogger<StateService>.Instance,
                pathResolver,
                encryption,
                steamAuthStorage);

            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state, new AppState());
            return state;
        }
    }
}
