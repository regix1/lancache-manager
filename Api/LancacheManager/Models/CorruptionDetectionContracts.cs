using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>Canonical typed report emitted by the Rust corruption detector.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CorruptionReport
{
    public const int SupportedContractVersion = 4;

    [JsonPropertyName("contract_version")]
    [JsonRequired]
    public int ContractVersion { get; set; }

    [JsonPropertyName("detection_method")]
    [JsonRequired]
    public CorruptionDetectionMethod DetectionMethod { get; set; }

    [JsonPropertyName("settings")]
    [JsonRequired]
    public CorruptionScanSettings Settings { get; set; } = new();

    [JsonPropertyName("scan_started_utc")]
    [JsonRequired]
    public string ScanStartedUtc { get; set; } = string.Empty;

    [JsonPropertyName("service_counts")]
    [JsonRequired]
    public Dictionary<string, long> ServiceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("detection_counts")]
    [JsonRequired]
    public Dictionary<string, long> DetectionCounts { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("coverage")]
    public CorruptionScanCoverage? Coverage { get; set; }

    [JsonPropertyName("total")]
    [JsonRequired]
    public long Total { get; set; }

    [JsonPropertyName("candidates")]
    [JsonRequired]
    public List<CorruptionCandidate> Candidates { get; set; } = [];
}

/// <summary>Method-specific settings echoed by the Rust report.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CorruptionScanSettings
{
    [JsonPropertyName("threshold")]
    public int? Threshold { get; set; }

    [JsonPropertyName("lookback_days")]
    public int? LookbackDays { get; set; }

    [JsonPropertyName("min_stable_age_seconds")]
    public ulong? MinimumStableAgeSeconds { get; set; }

    [JsonPropertyName("max_prefix_bytes")]
    public ulong? MaximumPrefixBytes { get; set; }
}

/// <summary>Aggregate scan coverage. Skips and I/O errors are diagnostics, never candidates.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CorruptionScanCoverage
{
    [JsonPropertyName("files_seen")]
    [JsonRequired]
    public long FilesSeen { get; set; }

    [JsonPropertyName("files_checked")]
    [JsonRequired]
    public long FilesChecked { get; set; }

    [JsonPropertyName("consistent")]
    [JsonRequired]
    public long Consistent { get; set; }

    [JsonPropertyName("skipped_by_reason")]
    [JsonRequired]
    public Dictionary<string, long> SkippedByReason { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("io_errors")]
    [JsonRequired]
    public long IoErrors { get; set; }

    [JsonPropertyName("bytes_read")]
    [JsonRequired]
    public long BytesRead { get; set; }

    [JsonPropertyName("sparse_files")]
    [JsonRequired]
    public long SparseFiles { get; set; }

    public void Add(CorruptionScanCoverage other)
    {
        FilesSeen = checked(FilesSeen + other.FilesSeen);
        FilesChecked = checked(FilesChecked + other.FilesChecked);
        Consistent = checked(Consistent + other.Consistent);
        IoErrors = checked(IoErrors + other.IoErrors);
        BytesRead = checked(BytesRead + other.BytesRead);
        SparseFiles = checked(SparseFiles + other.SparseFiles);
        foreach (var (reason, count) in other.SkippedByReason)
        {
            SkippedByReason[reason] = checked(SkippedByReason.GetValueOrDefault(reason) + count);
        }
    }
}

/// <summary>One immutable physical-file corruption candidate.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CorruptionCandidate
{
    [JsonPropertyName("candidate_id")]
    [JsonRequired]
    public string CandidateId { get; set; } = string.Empty;

    /// <summary>Attached by C# because Rust scans one datasource at a time.</summary>
    [JsonPropertyName("datasource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Datasource { get; set; }

    [JsonPropertyName("service")]
    [JsonRequired]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("exact_paths")]
    [JsonRequired]
    public List<string> ExactPaths { get; set; } = [];

    [JsonPropertyName("evidence")]
    [JsonRequired]
    public CorruptionEvidence Evidence { get; set; } = null!;
}

/// <summary>Closed, internally tagged evidence union.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RepeatedMissCorruptionEvidence), "repeated_miss")]
[JsonDerivedType(typeof(StructuralCorruptionEvidence), "structural")]
public abstract class CorruptionEvidence;

/// <summary>Threshold-qualified repeated-MISS evidence retained from contract v3.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RepeatedMissCorruptionEvidence : CorruptionEvidence
{
    [JsonPropertyName("raw_url")]
    [JsonRequired]
    public string RawUrl { get; set; } = string.Empty;

    [JsonPropertyName("normalized_uri")]
    [JsonRequired]
    public string NormalizedUri { get; set; } = string.Empty;

    [JsonPropertyName("observed_range")]
    [JsonRequired]
    public ObservedByteRange ObservedRange { get; set; } = new();

    [JsonPropertyName("cache_slice")]
    [JsonRequired]
    public CacheSliceIdentity CacheSlice { get; set; } = new();

    [JsonPropertyName("evidence_count")]
    [JsonRequired]
    public long EvidenceCount { get; set; }

    [JsonPropertyName("first_seen")]
    [JsonRequired]
    public string FirstSeen { get; set; } = string.Empty;

    [JsonPropertyName("last_seen")]
    [JsonRequired]
    public string LastSeen { get; set; } = string.Empty;

    [JsonPropertyName("observations")]
    [JsonRequired]
    public List<CandidateObservation> Observations { get; set; } = [];
}

/// <summary>Closed structural finding values emitted by the bounded v5 parser.</summary>
[JsonConverter(typeof(StructuralCorruptionIssueJsonConverter))]
public enum StructuralCorruptionIssue
{
    EmptyCacheFile,
    TruncatedCacheHeader,
    MalformedCacheHeader,
    InvalidPayloadOffset,
    TruncatedBeforePayload,
    CacheKeyPathMismatch,
    PayloadLengthMismatch,
    ContentRangeLengthMismatch,
    ContentLengthRangeConflict
}

internal sealed class StructuralCorruptionIssueJsonConverter : JsonConverter<StructuralCorruptionIssue>
{
    private static readonly Dictionary<string, StructuralCorruptionIssue> _fromWire =
        new Dictionary<string, StructuralCorruptionIssue>(StringComparer.Ordinal)
        {
            ["empty_cache_file"] = StructuralCorruptionIssue.EmptyCacheFile,
            ["truncated_cache_header"] = StructuralCorruptionIssue.TruncatedCacheHeader,
            ["malformed_cache_header"] = StructuralCorruptionIssue.MalformedCacheHeader,
            ["invalid_payload_offset"] = StructuralCorruptionIssue.InvalidPayloadOffset,
            ["truncated_before_payload"] = StructuralCorruptionIssue.TruncatedBeforePayload,
            ["cache_key_path_mismatch"] = StructuralCorruptionIssue.CacheKeyPathMismatch,
            ["payload_length_mismatch"] = StructuralCorruptionIssue.PayloadLengthMismatch,
            ["content_range_length_mismatch"] = StructuralCorruptionIssue.ContentRangeLengthMismatch,
            ["content_length_range_conflict"] = StructuralCorruptionIssue.ContentLengthRangeConflict
        };

    public override StructuralCorruptionIssue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !_fromWire.TryGetValue(reader.GetString() ?? string.Empty, out var issue))
        {
            throw new JsonException("Unknown structural corruption issue");
        }

        return issue;
    }

    public override void Write(
        Utf8JsonWriter writer,
        StructuralCorruptionIssue value,
        JsonSerializerOptions options)
    {
        var wire = _fromWire.FirstOrDefault(pair => pair.Value == value).Key
            ?? throw new JsonException("Unknown structural corruption issue");
        writer.WriteStringValue(wire);
    }
}

/// <summary>Stable physical identity captured while structural evidence was inspected.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StructuralFileFingerprint
{
    [JsonPropertyName("dev")]
    [JsonRequired]
    public ulong Device { get; set; }

    [JsonPropertyName("ino")]
    [JsonRequired]
    public ulong Inode { get; set; }

    [JsonPropertyName("len")]
    [JsonRequired]
    public ulong Length { get; set; }

    [JsonPropertyName("mtime_ns")]
    [JsonRequired]
    public long ModifiedNanoseconds { get; set; }

    [JsonPropertyName("ctime_ns")]
    [JsonRequired]
    public long ChangedNanoseconds { get; set; }
}

/// <summary>Deterministic structural cache-file evidence with no log observations.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StructuralCorruptionEvidence : CorruptionEvidence
{
    [JsonPropertyName("issues")]
    [JsonRequired]
    public List<StructuralCorruptionIssue> Issues { get; set; } = [];

    [JsonPropertyName("cache_key_encoding")]
    [JsonRequired]
    public string CacheKeyEncoding { get; set; } = string.Empty;

    [JsonPropertyName("cache_key")]
    [JsonRequired]
    public string CacheKey { get; set; } = string.Empty;

    [JsonPropertyName("cache_key_md5")]
    [JsonRequired]
    public string CacheKeyMd5 { get; set; } = string.Empty;

    [JsonPropertyName("cache_version")]
    [JsonRequired]
    public ulong CacheVersion { get; set; }

    [JsonPropertyName("http_status")]
    public int? HttpStatus { get; set; }

    [JsonPropertyName("header_start")]
    public ulong? HeaderStart { get; set; }

    [JsonPropertyName("body_start")]
    public ulong? BodyStart { get; set; }

    [JsonPropertyName("file_length")]
    [JsonRequired]
    public ulong FileLength { get; set; }

    [JsonPropertyName("actual_payload_length")]
    public ulong? ActualPayloadLength { get; set; }

    [JsonPropertyName("expected_payload_length")]
    public ulong? ExpectedPayloadLength { get; set; }

    [JsonPropertyName("content_length")]
    public ulong? ContentLength { get; set; }

    [JsonPropertyName("content_range")]
    public string? ContentRange { get; set; }

    [JsonPropertyName("fingerprint")]
    [JsonRequired]
    public StructuralFileFingerprint Fingerprint { get; set; } = new();

    [JsonPropertyName("detected_at_utc")]
    [JsonRequired]
    public string DetectedAtUtc { get; set; } = string.Empty;
}

/// <summary>Raw request range retained by the repeated-MISS detector.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ObservedByteRange
{
    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = "no_range";

    [JsonPropertyName("start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Start { get; set; }

    [JsonPropertyName("end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? End { get; set; }
}

/// <summary>Exact nginx cache slice identity retained by the repeated-MISS detector.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CacheSliceIdentity
{
    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = "no_range";

    [JsonPropertyName("start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Start { get; set; }

    [JsonPropertyName("end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? End { get; set; }
}

/// <summary>One qualifying access-log observation in a bounded evidence window.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CandidateObservation
{
    [JsonPropertyName("raw_url")]
    [JsonRequired]
    public string RawUrl { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    [JsonRequired]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("client_ip")]
    [JsonRequired]
    public string ClientIp { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    [JsonRequired]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("http_status")]
    [JsonRequired]
    public int HttpStatus { get; set; }

    [JsonPropertyName("cache_status")]
    [JsonRequired]
    public string CacheStatus { get; set; } = string.Empty;

    [JsonPropertyName("raw_range")]
    public string? RawRange { get; set; }

    [JsonPropertyName("bytes_served")]
    [JsonRequired]
    public long BytesServed { get; set; }
}

/// <summary>Exact server-owned stored evidence sent to Rust for one datasource.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CorruptionRemovalEvidence
{
    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; set; }

    [JsonPropertyName("detection_method")]
    public CorruptionDetectionMethod DetectionMethod { get; set; }

    [JsonPropertyName("scan_id")]
    public Guid ScanId { get; set; }

    [JsonPropertyName("threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Threshold { get; set; }

    [JsonPropertyName("datasource")]
    public string Datasource { get; set; } = string.Empty;

    [JsonPropertyName("candidates")]
    public List<CorruptionCandidate> Candidates { get; set; } = [];
}

/// <summary>Server-resolved, narrowing-only removal scope.</summary>
public sealed class CorruptionRemovalSelection
{
    public Guid ScanId { get; init; }
    public int Threshold { get; init; }
    public int ContractVersion { get; init; }
    public CorruptionDetectionMethod DetectionMethod { get; init; }
    public string Service { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, IReadOnlyList<CorruptionCandidate>> CandidatesByDatasource { get; init; } =
        new Dictionary<string, IReadOnlyList<CorruptionCandidate>>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasRepeatedMissEvidence => CandidatesByDatasource.Values
        .SelectMany(candidates => candidates)
        .Any(candidate => candidate.Evidence is RepeatedMissCorruptionEvidence);

    [JsonIgnore]
    public bool HasStructuralEvidence => CandidatesByDatasource.Values
        .SelectMany(candidates => candidates)
        .Any(candidate => candidate.Evidence is StructuralCorruptionEvidence);

    [JsonIgnore]
    public IReadOnlyList<string> CandidateIds => CandidatesByDatasource.Values
        .SelectMany(candidates => candidates)
        .Select(candidate => candidate.CandidateId)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
