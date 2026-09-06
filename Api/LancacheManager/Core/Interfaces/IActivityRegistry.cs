using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Single source of truth for "is this entity active/running right now" across every domain
/// (schedules, tracked operations, sessions, prefill, integrations, downloads). Domains report
/// transitions here; the registry serializes a full-snapshot <c>ActivityUpdated</c> SignalR broadcast
/// (mirroring <c>ServiceScheduleRegistry</c>'s SemaphoreSlim-serialized <c>SchedulesUpdated</c>)
/// so every green status dot reads one event. All members are safe to call concurrently from any thread.
/// </summary>
public interface IActivityRegistry
{
    /// <summary>
    /// Marks the (<paramref name="domain"/>, <paramref name="key"/>, <paramref name="aspect"/>) entry
    /// active or inactive. Broadcasts a fresh snapshot only when the effective state actually changed,
    /// so a redundant report is a no-op.
    /// </summary>
    Task ReportAsync(string domain, string key, string aspect, bool isActive, int activeCount = 1);

    /// <summary>
    /// Replaces the entire active set for one (<paramref name="domain"/>, <paramref name="aspect"/>)
    /// with exactly <paramref name="activeKeys"/> (key -&gt; active count). Keys previously active in
    /// this domain/aspect but absent here become inactive. For pollers that recompute a full set each
    /// tick (e.g. active downloads). Broadcasts only on a membership/count change.
    /// </summary>
    Task ReplaceAsync(string domain, string aspect, IReadOnlyDictionary<string, int> activeKeys);

    /// <summary>
    /// The current snapshot, used to seed a newly-connected client (SignalR cannot replay events the
    /// client missed before it connected).
    /// </summary>
    Task<ActivitySnapshot> GetSnapshotAsync();
}
