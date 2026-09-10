namespace LancacheManager.Core;

public readonly record struct IdentifiedCacheAggregate(
    ulong TotalBytes,
    ulong GameBytes,
    ulong ServiceBytes,
    int ActiveGameCount,
    int ActiveServiceCount);
