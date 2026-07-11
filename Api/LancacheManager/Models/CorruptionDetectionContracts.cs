using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>Canonical typed report emitted by the Rust corruption detector.</summary>
public sealed class CorruptionReport
{
    public const int SupportedContractVersion = 2;

    [JsonPropertyName("contract_version")]
    [JsonRequired]
    public int ContractVersion { get; set; }

    [JsonPropertyName("mode")]
    [JsonRequired]
    public CorruptionDetectionMode Mode { get; set; }

    [JsonPropertyName("threshold")]
    [JsonRequired]
    public int Threshold { get; set; }

    [JsonPropertyName("lookback_days")]
    [JsonRequired]
    public int LookbackDays { get; set; }

    [JsonPropertyName("scan_started_utc")]
    [JsonRequired]
    public string ScanStartedUtc { get; set; } = string.Empty;

    [JsonPropertyName("service_counts")]
    [JsonRequired]
    public Dictionary<string, long> ServiceCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("total")]
    [JsonRequired]
    public long Total { get; set; }

    [JsonPropertyName("removable_service_counts")]
    [JsonRequired]
    public Dictionary<string, long> RemovableServiceCounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("review_only_service_counts")]
    [JsonRequired]
    public Dictionary<string, long> ReviewOnlyServiceCounts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("removable_total")]
    [JsonRequired]
    public long RemovableTotal { get; set; }

    [JsonPropertyName("review_only_total")]
    [JsonRequired]
    public long ReviewOnlyTotal { get; set; }

    [JsonPropertyName("candidates")]
    [JsonRequired]
    public List<CorruptionCandidate> Candidates { get; set; } = [];
}

/// <summary>One immutable physical-slice corruption candidate.</summary>
public sealed class CorruptionCandidate
{
    [JsonPropertyName("candidate_id")]
    public string CandidateId { get; set; } = string.Empty;

    [JsonPropertyName("mode")]
    public CorruptionDetectionMode Mode { get; set; }

    [JsonPropertyName("threshold")]
    public int Threshold { get; set; }

    /// <summary>Attached by C# because Rust scans one datasource at a time.</summary>
    [JsonPropertyName("datasource")]
    public string Datasource { get; set; } = string.Empty;

    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("raw_url")]
    public string RawUrl { get; set; } = string.Empty;

    [JsonPropertyName("normalized_uri")]
    public string NormalizedUri { get; set; } = string.Empty;

    [JsonPropertyName("observed_range")]
    public ObservedByteRange ObservedRange { get; set; } = new();

    [JsonPropertyName("cache_slice")]
    public CacheSliceIdentity CacheSlice { get; set; } = new();

    [JsonPropertyName("exact_paths")]
    public List<string> ExactPaths { get; set; } = [];

    [JsonPropertyName("evidence_count")]
    public long EvidenceCount { get; set; }

    [JsonPropertyName("first_seen")]
    public string FirstSeen { get; set; } = string.Empty;

    [JsonPropertyName("last_seen")]
    public string LastSeen { get; set; } = string.Empty;

    [JsonPropertyName("retry_client")]
    public string? RetryClient { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("validation_state")]
    public string ValidationState { get; set; } = string.Empty;

    [JsonPropertyName("removal_allowed")]
    public bool RemovalAllowed { get; set; }

    [JsonPropertyName("observations")]
    public List<CandidateObservation> Observations { get; set; } = [];

    [JsonPropertyName("supporting_sibling")]
    [JsonRequired]
    public SupportingSiblingEvidence? SupportingSibling { get; set; }
}

/// <summary>Raw request range retained by the detector.</summary>
public sealed class ObservedByteRange
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "no_range";

    [JsonPropertyName("start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Start { get; set; }

    [JsonPropertyName("end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? End { get; set; }
}

/// <summary>Exact nginx cache slice identity retained by the detector.</summary>
public sealed class CacheSliceIdentity
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "no_range";

    [JsonPropertyName("start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Start { get; set; }

    [JsonPropertyName("end")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? End { get; set; }
}

/// <summary>
/// Safely validated present sibling supporting a review-only missing-slice finding.
/// It never authorizes removal of the absent target.
/// </summary>
public sealed class SupportingSiblingEvidence
{
    [JsonPropertyName("cache_slice")]
    [JsonRequired]
    public CacheSliceIdentity CacheSlice { get; set; } = new();

    [JsonPropertyName("exact_path")]
    [JsonRequired]
    public string ExactPath { get; set; } = string.Empty;
}

/// <summary>One qualifying access-log observation in a bounded evidence window.</summary>
public sealed class CandidateObservation
{
    /// <summary>
    /// Exact request-target spelling for this observation. New detector reports always
    /// populate it; an empty value represents legacy evidence that cannot be matched safely.
    /// </summary>
    [JsonPropertyName("raw_url")]
    public string RawUrl { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("client_ip")]
    public string ClientIp { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("http_status")]
    public int HttpStatus { get; set; }

    [JsonPropertyName("cache_status")]
    public string CacheStatus { get; set; } = string.Empty;

    [JsonPropertyName("raw_range")]
    public string? RawRange { get; set; }

    [JsonPropertyName("bytes_served")]
    [JsonRequired]
    public long BytesServed { get; set; }
}

/// <summary>
/// Exact stored evidence sent to Rust for one datasource. Paths and observations
/// are never accepted from the HTTP client.
/// </summary>
public sealed class CorruptionRemovalEvidence
{
    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; set; }

    [JsonPropertyName("scan_id")]
    public Guid ScanId { get; set; }

    [JsonPropertyName("mode")]
    public CorruptionDetectionMode Mode { get; set; }

    [JsonPropertyName("threshold")]
    public int Threshold { get; set; }

    [JsonPropertyName("datasource")]
    public string Datasource { get; set; } = string.Empty;

    [JsonPropertyName("candidates")]
    public List<CorruptionCandidate> Candidates { get; set; } = [];
}

/// <summary>Server-resolved, narrowing-only removal scope.</summary>
public sealed class CorruptionRemovalSelection
{
    public Guid ScanId { get; init; }
    public CorruptionDetectionMode Mode { get; init; }
    public int Threshold { get; init; }
    public int ContractVersion { get; init; }
    public string Service { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, IReadOnlyList<CorruptionCandidate>> CandidatesByDatasource { get; init; } =
        new Dictionary<string, IReadOnlyList<CorruptionCandidate>>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public IReadOnlyList<string> CandidateIds => CandidatesByDatasource.Values
        .SelectMany(candidates => candidates)
        .Select(candidate => candidate.CandidateId)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
