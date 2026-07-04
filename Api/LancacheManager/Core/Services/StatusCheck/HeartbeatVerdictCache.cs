using System.Collections.Concurrent;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Per-IP heartbeat verdict cache backing the sweep's active verification (contract amendment
/// v1.4): every unique resolved IP is probed at most once per TTL window no matter how many
/// domains resolve to it, concurrent requests for the same IP share one in-flight probe, and
/// probe fan-out is bounded. Test-domain requests reuse fresh sweep verdicts for free.
///
/// Probes run detached from caller cancellation (the probe function's own short HTTP timeout
/// bounds them) so one cancelled caller can never poison the shared cached task; callers still
/// honor their own token via <see cref="Task.WaitAsync(CancellationToken)"/>.
/// </summary>
internal sealed class HeartbeatVerdictCache
{
    private readonly Func<string, Task<HeartbeatResult>> _probe;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheEntry
    {
        public required Lazy<Task<HeartbeatResult>> Probe { get; init; }
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    }

    internal HeartbeatVerdictCache(Func<string, Task<HeartbeatResult>> probe, TimeSpan ttl, int maxConcurrency)
    {
        _probe = probe;
        _ttl = ttl;
        _concurrency = new SemaphoreSlim(maxConcurrency);
    }

    internal async Task<HeartbeatResult> GetAsync(string ip, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(ip, key => new CacheEntry
            {
                Probe = new Lazy<Task<HeartbeatResult>>(() => ProbeBoundedAsync(key))
            });

            if (DateTime.UtcNow - entry.CreatedAtUtc > _ttl)
            {
                // Expired: retire this entry and loop so exactly one caller seeds the replacement.
                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(ip, entry));
                continue;
            }

            return await entry.Probe.Value.WaitAsync(cancellationToken);
        }
    }

    private async Task<HeartbeatResult> ProbeBoundedAsync(string ip)
    {
        await _concurrency.WaitAsync();
        try
        {
            return await _probe(ip);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
