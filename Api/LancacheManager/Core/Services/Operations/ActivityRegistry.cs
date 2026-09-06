using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Serialized, in-memory implementation of <see cref="IActivityRegistry"/>. Every mutation and every
/// read runs under a single <see cref="SemaphoreSlim"/>, and the snapshot is taken and sent inside that
/// gate — so a concurrent report from another domain thread can never deliver an older snapshot last
/// and leave a finished entity stuck "active" (the exact reorder hazard the Schedules dot fixed).
/// Snapshots carry a process-monotonic <c>Revision</c> so the client can also drop a stale one.
/// </summary>
public class ActivityRegistry : IActivityRegistry
{
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<ActivityRegistry> _logger;

    // Active entries keyed by (domain, key, aspect) - a value tuple with structural equality, so no
    // composite string key is needed. Guarded by _gate; never read/written outside it.
    private readonly Dictionary<(string Domain, string Key, string Aspect), ActivityItem> _active = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _revision;

    public ActivityRegistry(ISignalRNotificationService notifications, ILogger<ActivityRegistry> logger)
    {
        _notifications = notifications;
        _logger = logger;
    }

    public async Task ReportAsync(string domain, string key, string aspect, bool isActive, int activeCount = 1)
    {
        await _gate.WaitAsync();
        try
        {
            if (ApplyLocked(domain, key, aspect, isActive, activeCount))
            {
                await BroadcastLockedAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAsync(string domain, string aspect, IReadOnlyDictionary<string, int> activeKeys)
    {
        await _gate.WaitAsync();
        try
        {
            var changed = false;

            // Drop any key currently active in this (domain, aspect) that is no longer present.
            var stale = _active.Keys
                .Where(k => k.Domain == domain && k.Aspect == aspect && !activeKeys.ContainsKey(k.Key))
                .ToList();
            foreach (var id in stale)
            {
                _active.Remove(id);
                changed = true;
            }

            foreach (var (key, count) in activeKeys)
            {
                if (ApplyLocked(domain, key, aspect, count > 0, count <= 0 ? 1 : count))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                await BroadcastLockedAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActivitySnapshot> GetSnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return SnapshotLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies one entry. Returns true when the stored state actually changed.</summary>
    private bool ApplyLocked(string domain, string key, string aspect, bool isActive, int activeCount)
    {
        var id = (domain, key, aspect);
        if (isActive && activeCount > 0)
        {
            if (_active.TryGetValue(id, out var existing) && existing.ActiveCount == activeCount)
            {
                return false;
            }

            _active[id] = new ActivityItem
            {
                Domain = domain,
                Key = key,
                Aspect = aspect,
                IsActive = true,
                ActiveCount = activeCount,
            };
            return true;
        }

        return _active.Remove(id);
    }

    private ActivitySnapshot SnapshotLocked()
    {
        var activities = _active.Values
            .OrderBy(v => v.Domain, StringComparer.Ordinal)
            .ThenBy(v => v.Key, StringComparer.Ordinal)
            .ThenBy(v => v.Aspect, StringComparer.Ordinal)
            .ToList();

        return new ActivitySnapshot { Revision = _revision, Activities = activities };
    }

    internal static ActivitySnapshot ToGuestVisibleSnapshot(ActivitySnapshot snapshot)
    {
        return new ActivitySnapshot
        {
            Revision = snapshot.Revision,
            Activities = snapshot.Activities
                .Where(a => a.Domain == ActivityDomains.Download)
                .ToList(),
        };
    }

    private async Task BroadcastLockedAsync()
    {
        _revision++;
        var snapshot = SnapshotLocked();
        try
        {
            await _notifications.NotifyAdminAsync(SignalREvents.ActivityUpdated, snapshot);
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed presence broadcast must never break the domain work that reported it.
            _logger.LogWarning(ex, "Failed to broadcast {Event} snapshot to admins", SignalREvents.ActivityUpdated);
        }

        try
        {
            var guestSnapshot = ToGuestVisibleSnapshot(snapshot);
            await _notifications.NotifyGuestAsync(SignalREvents.ActivityUpdated, guestSnapshot);
        }
        catch (Exception ex)
        {
            // Non-fatal: a failed presence broadcast must never break the domain work that reported it.
            _logger.LogWarning(ex, "Failed to broadcast {Event} snapshot to guests", SignalREvents.ActivityUpdated);
        }
    }
}
