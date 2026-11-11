using LancacheManager.Core.Services;

namespace LancacheManager.Models;

/// <summary>
/// What a request to the operation queue asks for beyond its type, scope and target. A request joins a waiting
/// or running one only when the two keys are equal. Each job type that has requests to tell apart builds its
/// key in one place, the record below named for it; every other job type passes <see cref="Plain"/>.
/// </summary>
public record OperationRequestKey
{
    protected OperationRequestKey()
    {
    }

    /// <summary>A request that its type, scope and target describe in full. A run registered without the
    /// queue carries this key too, because it does that plain work of its type.</summary>
    public static OperationRequestKey Plain { get; } = new();
}

/// <summary>A database reset: the whole database, or the tables it clears.</summary>
public sealed record DatabaseResetRequestKey : OperationRequestKey
{
    private DatabaseResetRequestKey(IReadOnlyList<string>? tables) => Tables = tables;

    /// <summary>The tables the reset clears, in ordinal order; null for a reset of the whole database.</summary>
    public IReadOnlyList<string>? Tables { get; }

    public static DatabaseResetRequestKey EveryTable { get; } = new((IReadOnlyList<string>?)null);

    /// <summary>The same tables in any order are the same reset.</summary>
    public static DatabaseResetRequestKey ForTables(IEnumerable<string> tables) =>
        new(tables.Order(StringComparer.Ordinal).ToArray());

    public bool Equals(DatabaseResetRequestKey? other) =>
        other is not null
        && (Tables is null
            ? other.Tables is null
            : other.Tables is not null && Tables.SequenceEqual(other.Tables, StringComparer.Ordinal));

    public override int GetHashCode() =>
        Tables?.Aggregate(0, (hash, table) => HashCode.Combine(hash, StringComparer.Ordinal.GetHashCode(table))) ?? 0;
}

/// <summary>An unmapped-file removal: other limits are other work.</summary>
public sealed record UnmappedFileRemovalRequestKey(UnmappedFilesLimits Limits) : OperationRequestKey;

/// <summary>A corruption removal: each detection method keeps its own current scan.</summary>
public sealed record CorruptionRemovalRequestKey(CorruptionDetectionMethod Method) : OperationRequestKey;

/// <summary>A corruption scan: the mode, miss threshold and lookback the scan service compares
/// (CorruptionDetectionService.StartDetectionAsync). The mode is null for a repeated-miss scan; the threshold
/// and lookback are null for a structural one.</summary>
public sealed record CorruptionDetectionRequestKey(StructuralScanMode? Mode, int? Threshold, int? LookbackDays)
    : OperationRequestKey;

/// <summary>A game detection: a full and an incremental scan are other work.</summary>
public sealed record GameDetectionRequestKey(DetectionScanType ScanType) : OperationRequestKey;

/// <summary>A game image fetch pass: a pass that refreshes Epic's image URLs is other work than one that
/// reads the stored URLs.</summary>
public sealed record GameImageFetchRequestKey(bool RefreshEpicImageUrls) : OperationRequestKey;

/// <summary>A depot mapping run: a crawl joins only a crawl of the same mode, and neither import joins a crawl
/// or the other import.</summary>
public sealed record DepotMappingRequestKey(DepotMappingWork Work) : OperationRequestKey
{
    public static DepotMappingRequestKey Crawl(bool incrementalOnly) =>
        new(incrementalOnly ? DepotMappingWork.IncrementalCrawl : DepotMappingWork.FullCrawl);
}

/// <summary>The work a depot mapping request asks for.</summary>
public enum DepotMappingWork
{
    FullCrawl,
    IncrementalCrawl,
    GitHubImport,
    LocalImport
}
