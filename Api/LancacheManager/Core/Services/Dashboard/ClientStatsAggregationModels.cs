namespace LancacheManager.Core.Services;

/// <summary>
/// One client IP's traffic totals, as produced by the SQL <c>GROUP BY ClientIp</c> over Downloads.
/// </summary>
public readonly record struct ClientIpAggregate(
    string ClientIp,
    long TotalCacheHitBytes,
    long TotalCacheMissBytes,
    int TotalDownloads,
    double TotalDurationSeconds,
    DateTime LastActivityUtc);
