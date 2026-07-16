using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Response for game removal operation start
/// </summary>
public class GameRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for game detection start
/// </summary>
public class GameDetectionStartResponse
{
    public string Message { get; set; } = string.Empty;
    public Guid OperationId { get; set; }
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}

/// <summary>
/// Response for active detection status
/// </summary>
public class ActiveDetectionResponse
{
    public bool IsProcessing { get; set; }
    public object? Operation { get; set; }

    /// <summary>
    /// Run-stable display flag for the active detection. Lifecycle events are always emitted so
    /// recovery works, but a silent automatic run reports false here so the recovery path can skip
    /// resurrecting a card on page reload instead of leaving it stuck once the silent terminal arrives.
    /// </summary>
    public bool ShowNotification { get; set; } = true;
}

/// <summary>
/// Response for cached detection results
/// </summary>
public class CachedDetectionResponse
{
    public bool HasCachedResults { get; set; }
    public object? Games { get; set; }
    public object? Services { get; set; }
    public int TotalGamesDetected { get; set; }
    public int TotalServicesDetected { get; set; }
    public string? LastDetectionTime { get; set; }

    /// <summary>
    /// Deduplicated total size of active (non-evicted) game cache files on disk.
    /// </summary>
    [JsonPropertyName("games_on_disk_bytes")]
    public ulong GamesOnDiskBytes { get; set; }

    /// <summary>
    /// Number of non-evicted games with cache files on disk.
    /// </summary>
    [JsonPropertyName("games_on_disk_count")]
    public int GamesOnDiskCount { get; set; }

    /// <summary>
    /// Deduplicated total size of matched game and service cache files on disk.
    /// </summary>
    [JsonPropertyName("identified_cache_bytes")]
    public ulong IdentifiedCacheBytes { get; set; }

    /// <summary>
    /// Portion of <see cref="IdentifiedCacheBytes"/> attributed to non-game services.
    /// </summary>
    [JsonPropertyName("identified_service_bytes")]
    public ulong IdentifiedServiceBytes { get; set; }

    /// <summary>
    /// When deduplicated on-disk totals were last computed from cache file paths.
    /// </summary>
    [JsonPropertyName("detection_summary_computed_at")]
    public string? DetectionSummaryComputedAt { get; set; }
}

/// <summary>
/// Response for cached corruption detection results
/// </summary>
public class CachedCorruptionResponse
{
    public bool HasCachedResults { get; set; }
    public Guid? ScanId { get; set; }
    public int? Threshold { get; set; }
    public int? LookbackDays { get; set; }
    public int? ContractVersion { get; set; }
    public string? DetectionMethod { get; set; }
    public string? ScanMode { get; set; }
    public CorruptionScanSettingsResponse? Settings { get; set; }
    public Dictionary<string, long>? CorruptionCounts { get; set; }
    public Dictionary<string, long>? DetectionCounts { get; set; }
    public CorruptionScanCoverageResponse? Coverage { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
    public string? LastDetectionTime { get; set; }
}

public sealed class CorruptionScanSettingsResponse
{
    public int? Threshold { get; set; }
    public int? LookbackDays { get; set; }
    public ulong? MinStableAgeSeconds { get; set; }
    public ulong? MaxPrefixBytes { get; set; }
}

public sealed class CorruptionScanCoverageResponse
{
    public long FilesSeen { get; set; }
    public long FilesChecked { get; set; }
    public long Consistent { get; set; }
    public long BytesRead { get; set; }
    public long SparseFiles { get; set; }
    public Dictionary<string, long> SkippedByReason { get; set; } = new(StringComparer.Ordinal);
    public long IoErrors { get; set; }

    public static CorruptionScanCoverageResponse? From(CorruptionScanCoverage? coverage) => coverage == null
        ? null
        : new CorruptionScanCoverageResponse
        {
            FilesSeen = coverage.FilesSeen,
            FilesChecked = coverage.FilesChecked,
            Consistent = coverage.Consistent,
            BytesRead = coverage.BytesRead,
            SparseFiles = coverage.SparseFiles,
            SkippedByReason = new Dictionary<string, long>(coverage.SkippedByReason, StringComparer.Ordinal),
            IoErrors = coverage.IoErrors
        };
}

/// <summary>
/// Wrapper for the bounded retained corruption scan history.
/// </summary>
public sealed class CorruptionScanHistoryResponse
{
    public IReadOnlyList<CorruptionScanHistoryEntryResponse> Scans { get; set; } = [];
}

/// <summary>
/// Summary for one retained corruption scan. Read-only behavior is enforced by
/// the dedicated history routes rather than a candidate-removal capability flag.
/// </summary>
public sealed class CorruptionScanHistoryEntryResponse
{
    public Guid ScanId { get; set; }
    public int ContractVersion { get; set; }
    public string DetectionMethod { get; set; } = string.Empty;
    public string? ScanMode { get; set; }
    public bool IsCurrent { get; set; }
    public string CompletedAtUtc { get; set; } = string.Empty;
    public CorruptionScanSettingsResponse Settings { get; set; } = new();
    public Dictionary<string, long> CorruptionCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);
    public CorruptionScanCoverageResponse? Coverage { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}

public sealed class CorruptionCandidateResponse
{
    public string CandidateId { get; set; } = string.Empty;
    public string Datasource { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public IReadOnlyList<string> ExactPaths { get; set; } = [];
    public object Evidence { get; set; } = null!;

    public static CorruptionCandidateResponse From(CorruptionCandidate candidate) => new()
    {
        CandidateId = candidate.CandidateId,
        Datasource = candidate.Datasource ?? string.Empty,
        Service = candidate.Service,
        ExactPaths = candidate.ExactPaths,
        Evidence = candidate.Evidence switch
        {
            RepeatedMissCorruptionEvidence repeatedMiss => RepeatedMissCorruptionEvidenceResponse.From(repeatedMiss),
            StructuralCorruptionEvidence structural => StructuralCorruptionEvidenceResponse.From(structural),
            _ => throw new InvalidDataException("Stored corruption candidate has an unsupported evidence branch")
        }
    };
}

public sealed class RepeatedMissCorruptionEvidenceResponse
{
    public string Kind { get; set; } = "repeated_miss";
    public string RawUrl { get; set; } = string.Empty;
    public string NormalizedUri { get; set; } = string.Empty;
    public ByteRangeResponse ObservedRange { get; set; } = new();
    public ByteRangeResponse CacheSlice { get; set; } = new();
    public long EvidenceCount { get; set; }
    public string FirstSeen { get; set; } = string.Empty;
    public string LastSeen { get; set; } = string.Empty;
    public IReadOnlyList<CandidateObservationResponse> Observations { get; set; } = [];

    public static RepeatedMissCorruptionEvidenceResponse From(RepeatedMissCorruptionEvidence evidence) => new()
    {
        RawUrl = evidence.RawUrl,
        NormalizedUri = evidence.NormalizedUri,
        ObservedRange = ByteRangeResponse.From(evidence.ObservedRange),
        CacheSlice = ByteRangeResponse.From(evidence.CacheSlice),
        EvidenceCount = evidence.EvidenceCount,
        FirstSeen = evidence.FirstSeen,
        LastSeen = evidence.LastSeen,
        Observations = evidence.Observations.Select(CandidateObservationResponse.From).ToList()
    };
}

public sealed class StructuralCorruptionEvidenceResponse
{
    public string Kind { get; set; } = "structural";
    public IReadOnlyList<StructuralCorruptionIssue> Issues { get; set; } = [];
    public string CacheKeyEncoding { get; set; } = string.Empty;
    public string CacheKey { get; set; } = string.Empty;
    public string CacheKeyMd5 { get; set; } = string.Empty;
    public ulong CacheVersion { get; set; }
    public int? HttpStatus { get; set; }
    public ulong? HeaderStart { get; set; }
    public ulong? BodyStart { get; set; }
    public ulong FileLength { get; set; }
    public ulong? ActualPayloadLength { get; set; }
    public ulong? ExpectedPayloadLength { get; set; }
    public ulong? ContentLength { get; set; }
    public string? ContentRange { get; set; }
    public StructuralFileFingerprintResponse Fingerprint { get; set; } = new();
    public string DetectedAtUtc { get; set; } = string.Empty;

    public static StructuralCorruptionEvidenceResponse From(StructuralCorruptionEvidence evidence) => new()
    {
        Issues = evidence.Issues,
        CacheKeyEncoding = evidence.CacheKeyEncoding,
        CacheKey = evidence.CacheKey,
        CacheKeyMd5 = evidence.CacheKeyMd5,
        CacheVersion = evidence.CacheVersion,
        HttpStatus = evidence.HttpStatus,
        HeaderStart = evidence.HeaderStart,
        BodyStart = evidence.BodyStart,
        FileLength = evidence.FileLength,
        ActualPayloadLength = evidence.ActualPayloadLength,
        ExpectedPayloadLength = evidence.ExpectedPayloadLength,
        ContentLength = evidence.ContentLength,
        ContentRange = evidence.ContentRange,
        Fingerprint = StructuralFileFingerprintResponse.From(evidence.Fingerprint),
        DetectedAtUtc = evidence.DetectedAtUtc
    };
}

public sealed class StructuralFileFingerprintResponse
{
    public ulong Dev { get; set; }
    public ulong Ino { get; set; }
    public ulong Len { get; set; }
    public long MtimeNs { get; set; }
    public long CtimeNs { get; set; }

    public static StructuralFileFingerprintResponse From(StructuralFileFingerprint fingerprint) => new()
    {
        Dev = fingerprint.Device,
        Ino = fingerprint.Inode,
        Len = fingerprint.Length,
        MtimeNs = fingerprint.ModifiedNanoseconds,
        CtimeNs = fingerprint.ChangedNanoseconds
    };
}

public sealed class ByteRangeResponse
{
    public string Kind { get; set; } = "no_range";
    public ulong? Start { get; set; }
    public ulong? End { get; set; }

    public static ByteRangeResponse From(ObservedByteRange range) => new()
    {
        Kind = range.Kind,
        Start = range.Start,
        End = range.End
    };

    public static ByteRangeResponse From(CacheSliceIdentity range) => new()
    {
        Kind = range.Kind,
        Start = range.Start,
        End = range.End
    };
}

public sealed class CandidateObservationResponse
{
    public string RawUrl { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int HttpStatus { get; set; }
    public string CacheStatus { get; set; } = string.Empty;
    public string? RawRange { get; set; }
    public long BytesServed { get; set; }

    public static CandidateObservationResponse From(CandidateObservation observation) => new()
    {
        RawUrl = observation.RawUrl,
        Timestamp = observation.Timestamp,
        ClientIp = observation.ClientIp,
        Method = observation.Method,
        HttpStatus = observation.HttpStatus,
        CacheStatus = observation.CacheStatus,
        RawRange = observation.RawRange,
        BytesServed = observation.BytesServed
    };
}

/// <summary>
/// Response for game image errors
/// </summary>
public class GameImageErrorResponse
{
    public string Error { get; set; } = string.Empty;
}
