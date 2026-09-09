using LancacheManager.Infrastructure.Services;
using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class LogRemovalProgressTests
{
    [Theory]
    [InlineData(0, 2, 0, 0)]
    [InlineData(0, 2, 100, 47.5)]
    [InlineData(1, 2, 0, 47.5)]
    [InlineData(1, 2, 100, 95)]
    public void MultiDatasourcePercentUsesMonotonicBands(
        int index,
        int count,
        double inner,
        double expected)
    {
        Assert.Equal(expected, RustLogRemovalService.ScaleIntoBand(inner, index, count, 95), 6);
    }

    [Fact]
    public void CountersAreCumulativeAcrossDatasources()
    {
        var cumulative = RustLogRemovalService.AddCumulativeCounters(
            completedFiles: 10,
            completedLines: 1_000,
            completedRemoved: 250,
            currentFiles: 3,
            currentLines: 400,
            currentRemoved: 100);

        Assert.Equal(13, cumulative.Files);
        Assert.Equal(1_400, cumulative.Lines);
        Assert.Equal(350, cumulative.Removed);
    }

    [Fact]
    public async Task ExternalCompletion_RejectsOldProgressAndPreservesTheNextRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-removal-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "GetOperationsDirectory"));
        try
        {
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "default",
                ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                ["LanCache:DataSources:0:Enabled"] = "true"
            }).Build();
            var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RemovalMessages>();
            var messages = (RemovalMessages)(object)notifications;
            var removal = new RustLogRemovalService(NullLogger<RustLogRemovalService>.Instance,
                paths, notifications, null!,
                new RustProcessHelper(NullLogger<RustProcessHelper>.Instance,
                    new ProcessManager(NullLogger<ProcessManager>.Instance), paths, tracker),
                null!, null!, new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance),
                tracker, null!);
            var sourceField = typeof(RustLogRemovalService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var start = typeof(RustLogRemovalService).GetMethod("StartRemovalAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var firstTask = (Task<bool>)start.Invoke(removal, ["steam"])!;
            var first = await messages.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var firstRegisteredSource = tracker.GetOperation(first)!.CancellationTokenSource!;
            using var firstCleanupSource = new DisposeTrackingCancellationTokenSource();
            sourceField.SetValue(removal, firstCleanupSource);
            tracker.ForceKillOperation(first);
            tracker.CompleteOperation(first, false, cancelled: true);
            Assert.Throws<ObjectDisposedException>(() => _ = firstRegisteredSource.Token);
            Assert.Equal(0, firstCleanupSource.DisposeCalls);
            var nextTask = (Task<bool>)start.Invoke(removal, ["epicgames"])!;
            var next = await messages.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var nextRegisteredSource = tracker.GetOperation(next)!.CancellationTokenSource!;
            using var nextCleanupSource = new DisposeTrackingCancellationTokenSource();
            sourceField.SetValue(removal, nextCleanupSource);
            messages.Release[first].TrySetResult();
            Assert.False(await firstTask.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(next, removal.CurrentOperationId);
            Assert.True(removal.IsProcessing);
            Assert.Equal("epicgames", removal.CurrentService);
            Assert.Equal(0, removal.GetRemovalStatus().LinesRemoved);
            var complete = Assert.Single(messages.Completions);
            Assert.Equal(first, complete.OperationId);
            Assert.Equal("steam", complete.Service);
            Assert.True(complete.Cancelled);
            Assert.DoesNotContain(messages.Progress, id => id == first);

            tracker.ForceKillOperation(next);
            tracker.CompleteOperation(next, false, cancelled: true);
            Assert.Throws<ObjectDisposedException>(() => _ = nextRegisteredSource.Token);
            Assert.Equal(0, nextCleanupSource.DisposeCalls);
            messages.Release[next].TrySetResult();
            Assert.False(await nextTask.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public class RemovalMessages : DispatchProxy
    {
        public System.Threading.Channels.Channel<Guid> Started { get; } = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        public ConcurrentDictionary<Guid, TaskCompletionSource> Release { get; } = new();
        public ConcurrentQueue<SignalRNotifications.LogRemovalComplete> Completions { get; } = new();
        public ConcurrentQueue<Guid> Progress { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                var eventName = (string)args![0]!;
                var value = args[1]!;
                if (value is SignalRNotifications.LogRemovalComplete completion)
                    Completions.Enqueue(completion);
                if (eventName == "LogRemovalStarted")
                {
                    var id = (Guid)value.GetType().GetProperty("OperationId")!.GetValue(value)!;
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Release[id] = release;
                    Started.Writer.TryWrite(id);
                    return release.Task;
                }
                if (eventName == "LogRemovalProgress")
                    Progress.Enqueue((Guid)value.GetType().GetProperty("OperationId")!.GetValue(value)!);
                return Task.CompletedTask;
            }
            return targetMethod.ReturnType == typeof(Task) ? Task.CompletedTask : null;
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
}
