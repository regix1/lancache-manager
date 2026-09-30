using LancacheManager.Core.Interfaces;

namespace LancacheManager.Core.Services;

public partial class DashboardBatchService
{
    /// <summary>
    /// One identity-and-client aggregate of the recent pass. It carries the mapping columns as
    /// well as the sums because <see cref="GameNameResolver"/> resolves names straight onto it, so
    /// one representative object per group answers both the fold's key and its totals.
    /// </summary>
    internal sealed class DashboardGroupRow : IGameNameRow
    {
        public long? DepotId { get; init; }
        public string ClientIp { get; init; } = string.Empty;
        public string Service { get; init; } = string.Empty;
        public string? GameName { get; set; }
        public long? GameAppId { get; set; }
        public string? EpicAppId { get; init; }
        public string? XboxProductId { get; init; }
        public long CacheHitBytes { get; init; }
        public long CacheMissBytes { get; init; }
        public DateTime LastStartTimeUtc { get; init; }
        public DateTime LastEndTimeUtc { get; init; }
        /// <summary>Earliest start in the group. Unlike the latest one it does not move while the
        /// group is still downloading, which is what the panel orders its running rows by.</summary>
        public DateTime FirstStartTimeUtc { get; init; }
        public int RequestCount { get; init; }
        public int EvictedCount { get; init; }
        public int ActiveCount { get; init; }
    }
}
