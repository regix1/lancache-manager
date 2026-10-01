using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Builds a <see cref="CacheScanGate"/> over a speed tracker whose snapshot the caller chooses,
/// so a test can put the server in the "a client download is writing" state without a Rust
/// process. Every service and controller that takes the gate needs one of these, and most of them
/// only need it to say "nothing is downloading".
/// </summary>
internal static class CacheScanGateHarness
{
    internal static CacheScanGate Idle() => With(new DownloadSpeedSnapshot());

    internal static CacheScanGate Downloading()
    {
        var snapshot = new DownloadSpeedSnapshot();
        MakeBusy(snapshot);
        return With(snapshot);
    }

    internal static CacheScanGate With(DownloadSpeedSnapshot snapshot)
        => GateOver(TrackerWith(snapshot, []));

    /// <summary>
    /// Builds a gate over <paramref name="tracker"/> and puts back the process-wide hooks its
    /// constructor installs on the speed tracker. Nineteen test files build throwaway gates through
    /// this harness, all of them for the gate itself rather than for those hooks, and without the
    /// restore the last one to run decides what the tracker asks: a test that drives the hooks would
    /// get its answer from another class's discarded tracker. Same discipline ScheduleRunGateTests
    /// applies to the schedule gate.
    /// </summary>
    internal static CacheScanGate GateOver(RustSpeedTrackerService tracker)
    {
        var previousAnswer = RustSpeedTrackerService.ScanBlockedAnswer;
        var previousDelay = RustSpeedTrackerService.ScanBlockedRecheckDelay;
        try
        {
            return new CacheScanGate(tracker, NullLogger<CacheScanGate>.Instance);
        }
        finally
        {
            RustSpeedTrackerService.ScanBlockedAnswer = previousAnswer;
            RustSpeedTrackerService.ScanBlockedRecheckDelay = previousDelay;
        }
    }

    internal static RustSpeedTrackerService TrackerWith(
        DownloadSpeedSnapshot snapshot,
        IReadOnlyCollection<string> hiddenClientIps,
        TimeProvider? clock = null,
        DatasourceService? datasources = null)
    {
        datasources ??= DatasourceServiceWith(
            ("disabled",
             Path.Combine(Path.GetTempPath(), "lancache-manager-tests", "disabled-cache"),
             Path.Combine(Path.GetTempPath(), "lancache-manager-tests", "disabled-logs"),
             false,
             "auto"));
        snapshot.Version = 2;
        snapshot.StreamId = string.IsNullOrWhiteSpace(snapshot.StreamId) ? "test-stream" : snapshot.StreamId;
        snapshot.WindowSeconds = 2;
        var tracker = (RustSpeedTrackerService)RuntimeHelpers.GetUninitializedObject(typeof(RustSpeedTrackerService));
        SetField(tracker, "_snapshotLock", new object());
        SetField(tracker, "_logger", NullLogger<RustSpeedTrackerService>.Instance);
        // Nothing built this way runs a constructor, so every lock the tracker takes has to be
        // supplied here or the first announcement fails on a null.
        SetField(tracker, "_scanBlockedLock", new object());
        SetField(tracker, "_currentSnapshot", snapshot);
        SetField(tracker, "_datasourceService", datasources);
        SetField(tracker, "_capabilityService", new DatasourceCapabilityService(datasources));
        SetField(tracker, "_stateService", StateServiceHiding(hiddenClientIps));
        SetField(tracker, "_notifications", CreateProxy<ISignalRNotificationService>((method, _) =>
            method.ReturnType == typeof(Task) ? Task.CompletedTask : null));
        SetField(tracker, "_clock", clock ?? TimeProvider.System);
        SetField(tracker, "_streamId", snapshot.StreamId);
        SetField(tracker, "_revision", snapshot.Revision);
        SetField(tracker, "_agingUtc", snapshot.TimestampUtc);
        SetField(tracker, "_visibilityMark", string.Empty);
        SetField(tracker, "_ageWake", new SemaphoreSlim(0, 1));
        SetField(tracker, "_publication", Channel.CreateBounded<DownloadSpeedSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }));
        SetField(tracker, "_runSources", new Dictionary<Guid, Dictionary<string, string>>());
        SetField(tracker, "_sourceRuns", new Dictionary<string, Guid>(StringComparer.Ordinal));
        SetField(tracker, "_currentSources", null);
        SetField(tracker, "_currentSourcesBuiltUtc", default(DateTime));
        return tracker;
    }

    internal static BlockingSnapshotChannel BlockFirstPublication(RustSpeedTrackerService tracker)
    {
        var publication = new BlockingSnapshotChannel();
        SetField(tracker, "_publication", publication);
        return publication;
    }

    internal static List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>? PrepareNativeSnapshot(
        RustSpeedTrackerService tracker,
        DownloadSpeedSnapshot snapshot,
        Guid runId,
        Dictionary<string, string> captured,
        Dictionary<string, string> currentSources,
        DateTime nowUtc)
    {
        var method = typeof(RustSpeedTrackerService).GetMethod(
            "PrepareNativeSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return (List<(GameSpeedInfo Game, DownloadSource Source, Guid RunId)>?)method!.Invoke(
            tracker,
            [snapshot, runId, captured, currentSources, nowUtc]);
    }

    internal static DatasourceService DatasourceServiceWith(
        params (string Name, string CachePath, string LogPath, bool Enabled, string SchemeOverride)[] sources)
    {
        var settings = new Dictionary<string, string?>();
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            settings[$"LanCache:DataSources:{index}:Name"] = source.Name;
            settings[$"LanCache:DataSources:{index}:CachePath"] = source.CachePath;
            settings[$"LanCache:DataSources:{index}:LogPath"] = source.LogPath;
            settings[$"LanCache:DataSources:{index}:Enabled"] = source.Enabled.ToString();
            settings[$"LanCache:DataSources:{index}:SchemeOverride"] = source.SchemeOverride;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var paths = CreateProxy<IPathResolver>((method, args) => method.Name switch
        {
            nameof(IPathResolver.ResolvePath) => Path.GetFullPath((string)args![0]!),
            nameof(IPathResolver.NormalizePath) => Path.GetFullPath((string)args![0]!),
            nameof(IPathResolver.IsDirectoryWritable) => false,
            _ when method.ReturnType == typeof(bool) => false,
            _ when method.ReturnType == typeof(int) => 0,
            _ => Path.GetTempPath(),
        });
        return new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
    }

    internal static Dictionary<string, string> CaptureRoots(DatasourceService datasources) =>
        datasources.GetDatasources().ToDictionary(
            source => source.Name,
            source => source.LogPath,
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Hides no client and rewrites no evicted data, so a snapshot survives the client-visible
    /// projection exactly as it was written.
    /// </summary>
    internal static IStateService VisibleClientsStateService() => StateServiceHiding([]);

    /// <summary>
    /// Hides the named clients and rewrites no evicted data, so everything else survives the
    /// client-visible projection exactly as it was written.
    /// </summary>
    internal static IStateService StateServiceHiding(IReadOnlyCollection<string> hiddenClientIps)
        => CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => hiddenClientIps.ToList(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            _ => null
        });

    /// <summary>
    /// Replaces the fields of an already-built snapshot, so a gate handed to production code
    /// before a download starts reports the download once it does.
    /// </summary>
    internal static void MakeBusy(DownloadSpeedSnapshot snapshot)
    {
        var now = DateTime.UtcNow;
        var measuredUntilUtc = now.AddSeconds(2);
        var activeUntilUtc = now.AddSeconds(15);
        snapshot.Version = 2;
        snapshot.StreamId = string.IsNullOrWhiteSpace(snapshot.StreamId) ? "test-stream" : snapshot.StreamId;
        snapshot.Revision++;
        snapshot.TimestampUtc = now;
        snapshot.IsAvailable = true;
        snapshot.WindowSeconds = 2;
        // The tracker publishes this count; only the client-visible projection recomputes it, so a
        // raw snapshot that omits it reads as idle.
        snapshot.EntriesInWindow = 4;
        snapshot.GameSpeeds =
        [
            new GameSpeedInfo
            {
                Key = "steam|10.0.0.5|service",
                Service = "steam",
                ClientIp = "10.0.0.5",
                BytesPerSecond = 1_000_000,
                TotalBytes = 2_000_000,
                RequestCount = 4,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                ActiveUntilUtc = activeUntilUtc,
                Sources =
                [
                    new DownloadSource
                    {
                        Datasources = ["test"],
                        FirstSeenUtc = now,
                        LastSeenUtc = now,
                        MeasuredUntilUtc = measuredUntilUtc,
                        ActiveUntilUtc = activeUntilUtc,
                        BytesPerSecond = 1_000_000,
                        TotalBytes = 2_000_000,
                        RequestCount = 4,
                    },
                ],
            },
        ];
        snapshot.ClientSpeeds =
        [
            new ClientSpeedInfo
            {
                ClientIp = "10.0.0.5",
                BytesPerSecond = 1_000_000,
                TotalBytes = 2_000_000,
                ActiveGames = 1,
                ActiveUntilUtc = activeUntilUtc,
            },
        ];
    }

    /// <summary>
    /// The counterpart to <see cref="MakeBusy"/>: clears the entry count as well as the lists,
    /// because the raw snapshot carries its own count rather than deriving it from them.
    /// </summary>
    internal static void MakeIdle(DownloadSpeedSnapshot snapshot)
    {
        snapshot.Revision++;
        snapshot.TimestampUtc = DateTime.UtcNow;
        snapshot.EntriesInWindow = 0;
        snapshot.TotalBytesPerSecond = 0;
        snapshot.GameSpeeds = [];
        snapshot.ClientSpeeds = [];
    }

    internal static void SetEmptyInstance(object instance, string fieldName)
    {
        var field = FindField(instance, fieldName);
        field.SetValue(instance, Activator.CreateInstance(field.FieldType));
    }

    internal static void SetField(object instance, string fieldName, object? value)
        => FindField(instance, fieldName).SetValue(instance, value);

    private static FieldInfo FindField(object instance, string fieldName)
    {
        var type = instance.GetType();
        while (type != null)
        {
            var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
            {
                return field;
            }
            type = type.BaseType;
        }

        throw new InvalidOperationException($"Field {fieldName} was not found on {instance.GetType().Name}");
    }

    internal static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }

    internal sealed class BlockingSnapshotChannel : Channel<DownloadSpeedSnapshot>, IDisposable
    {
        private readonly ManualResetEventSlim _firstWrite = new(false);
        private readonly ManualResetEventSlim _secondWrite = new(false);
        private readonly ManualResetEventSlim _releaseFirst = new(false);

        internal BlockingSnapshotChannel()
        {
            var inner = Channel.CreateBounded<DownloadSpeedSnapshot>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            Reader = inner.Reader;
            Writer = new BlockingSnapshotWriter(
                inner.Writer,
                _firstWrite,
                _secondWrite,
                _releaseFirst);
        }

        internal bool WaitForFirstWrite(TimeSpan timeout) => _firstWrite.Wait(timeout);

        internal bool WaitForSecondWrite(TimeSpan timeout) => _secondWrite.Wait(timeout);

        internal void ReleaseFirstWrite() => _releaseFirst.Set();

        public void Dispose()
        {
            _releaseFirst.Set();
            _firstWrite.Dispose();
            _secondWrite.Dispose();
            _releaseFirst.Dispose();
        }

        private sealed class BlockingSnapshotWriter(
            ChannelWriter<DownloadSpeedSnapshot> writer,
            ManualResetEventSlim firstWrite,
            ManualResetEventSlim secondWrite,
            ManualResetEventSlim releaseFirst)
            : ChannelWriter<DownloadSpeedSnapshot>
        {
            private int _writeCount;

            public override bool TryComplete(Exception? error = null) => writer.TryComplete(error);

            public override bool TryWrite(DownloadSpeedSnapshot item)
            {
                var writeCount = Interlocked.Increment(ref _writeCount);
                if (writeCount == 1)
                {
                    firstWrite.Set();
                    releaseFirst.Wait();
                }
                else if (writeCount == 2)
                {
                    secondWrite.Set();
                }

                return writer.TryWrite(item);
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
                writer.WaitToWriteAsync(cancellationToken);
        }
    }
}
