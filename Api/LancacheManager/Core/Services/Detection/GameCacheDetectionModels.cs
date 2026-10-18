using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>The files and bytes the person confirmed an unmapped removal may delete.</summary>
public sealed record UnmappedFilesLimits(long MaxFiles, long MaxBytes);

/// <summary>What one unmapped-files pass found or removed, summed over every datasource.</summary>
public sealed record UnmappedFilesPassResult(
    int EligibleFiles,
    long EligibleBytes,
    IReadOnlyList<UnmappedService> EligibleByService,
    long RemovedFiles,
    long RemovedBytes,
    long RemoveFailures,
    bool OverLimit);
