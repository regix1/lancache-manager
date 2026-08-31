using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
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
        DownloadSpeedSnapshot snapshot, IReadOnlyCollection<string> hiddenClientIps)
    {
        var tracker = (RustSpeedTrackerService)RuntimeHelpers.GetUninitializedObject(typeof(RustSpeedTrackerService));
        SetField(tracker, "_snapshotLock", new object());
        // Nothing built this way runs a constructor, so every lock the tracker takes has to be
        // supplied here or the first announcement fails on a null.
        SetField(tracker, "_scanBlockedLock", new object());
        SetField(tracker, "_currentSnapshot", snapshot);
        SetField(tracker, "_stateService", StateServiceHiding(hiddenClientIps));
        return tracker;
    }

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
            _ => null
        });

    /// <summary>
    /// Replaces the fields of an already-built snapshot, so a gate handed to production code
    /// before a download starts reports the download once it does.
    /// </summary>
    internal static void MakeBusy(DownloadSpeedSnapshot snapshot)
    {
        snapshot.WindowSeconds = 2;
        // The tracker publishes this count; only the client-visible projection recomputes it, so a
        // raw snapshot that omits it reads as idle.
        snapshot.EntriesInWindow = 4;
        snapshot.GameSpeeds = [new GameSpeedInfo { Service = "steam", ClientIp = "10.0.0.5", RequestCount = 4 }];
        snapshot.ClientSpeeds = [new ClientSpeedInfo { ClientIp = "10.0.0.5", BytesPerSecond = 1_000_000 }];
    }

    /// <summary>
    /// The counterpart to <see cref="MakeBusy"/>: clears the entry count as well as the lists,
    /// because the raw snapshot carries its own count rather than deriving it from them.
    /// </summary>
    internal static void MakeIdle(DownloadSpeedSnapshot snapshot)
    {
        snapshot.EntriesInWindow = 0;
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

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
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
}
