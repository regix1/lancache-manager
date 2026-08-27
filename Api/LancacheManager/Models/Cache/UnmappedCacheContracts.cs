using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>Canonical typed report emitted by the Rust unmapped-cache scan.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UnmappedScanReport
{
    public const int SupportedContractVersion = 1;

    [JsonPropertyName("contract_version")]
    [JsonRequired]
    public int ContractVersion { get; set; }

    [JsonPropertyName("cancelled")]
    [JsonRequired]
    public bool Cancelled { get; set; }

    [JsonPropertyName("scan_started_utc")]
    [JsonRequired]
    public string ScanStartedUtc { get; set; } = string.Empty;

    [JsonPropertyName("cache_root")]
    [JsonRequired]
    public string CacheRoot { get; set; } = string.Empty;

    [JsonPropertyName("files_on_disk")]
    [JsonRequired]
    public long FilesOnDisk { get; set; }

    [JsonPropertyName("claimed_digests")]
    [JsonRequired]
    public long ClaimedDigests { get; set; }

    [JsonPropertyName("skipped_non_hash_names")]
    [JsonRequired]
    public long SkippedNonHashNames { get; set; }

    [JsonPropertyName("orphan_count")]
    [JsonRequired]
    public long OrphanCount { get; set; }

    [JsonPropertyName("orphan_bytes")]
    [JsonRequired]
    public long OrphanBytes { get; set; }

    [JsonPropertyName("unreadable_keys")]
    [JsonRequired]
    public long UnreadableKeys { get; set; }

    [JsonPropertyName("services")]
    [JsonRequired]
    public List<UnmappedServiceGroup> Services { get; set; } = [];
}

/// <summary>One service's unmapped files within a scan report.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UnmappedServiceGroup
{
    [JsonPropertyName("service")]
    [JsonRequired]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("file_count")]
    [JsonRequired]
    public long FileCount { get; set; }

    [JsonPropertyName("total_bytes")]
    [JsonRequired]
    public long TotalBytes { get; set; }

    [JsonPropertyName("files")]
    [JsonRequired]
    public List<UnmappedCacheFile> Files { get; set; } = [];
}

/// <summary>
/// One cache file on disk that no game or service detection row claims. Serialized
/// verbatim into <see cref="CachedUnmappedDetection.FilesJson"/>, so the property names are
/// the scanner's snake_case contract on both hops.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UnmappedCacheFile
{
    [JsonPropertyName("digest")]
    [JsonRequired]
    public string Digest { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    [JsonRequired]
    public string Path { get; set; } = string.Empty;

    /// <summary>Upstream URL from the stored nginx KEY header, null when unreadable.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("size_bytes")]
    [JsonRequired]
    public long SizeBytes { get; set; }
}

/// <summary>
/// Paths handed to the Rust remover. Written by C# and read with deny_unknown_fields,
/// so it carries these two members and nothing else.
/// </summary>
public sealed class UnmappedRemovalRequest
{
    [JsonPropertyName("contract_version")]
    public int ContractVersion { get; set; }

    [JsonPropertyName("paths")]
    public List<string> Paths { get; set; } = [];
}

/// <summary>Canonical typed report emitted by the Rust unmapped-cache removal.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UnmappedRemovalReport
{
    [JsonPropertyName("contract_version")]
    [JsonRequired]
    public int ContractVersion { get; set; }

    [JsonPropertyName("cancelled")]
    [JsonRequired]
    public bool Cancelled { get; set; }

    [JsonPropertyName("deleted_files")]
    [JsonRequired]
    public long DeletedFiles { get; set; }

    [JsonPropertyName("already_missing")]
    [JsonRequired]
    public long AlreadyMissing { get; set; }

    /// <summary>Refused because a detection run claimed the digest between scan and delete.</summary>
    [JsonPropertyName("claimed_since_scan")]
    [JsonRequired]
    public long ClaimedSinceScan { get; set; }

    [JsonPropertyName("bytes_freed")]
    [JsonRequired]
    public long BytesFreed { get; set; }
}

/// <summary>Projection of the stored unmapped-cache scan for the management section.</summary>
public sealed class UnmappedCacheResponse
{
    public bool HasCachedResults { get; set; }

    /// <summary>
    /// Defaults to the version this build writes so an empty response reads as "nothing stored"
    /// rather than as a version the reader has to reject.
    /// </summary>
    public int ContractVersion { get; set; } = UnmappedScanReport.SupportedContractVersion;

    public Guid? ScanId { get; set; }
    public string? LastScanTime { get; set; }
    public long TotalFiles { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>
    /// One entry per service. A single list rather than parallel count maps, so a service
    /// cannot carry a file count without a byte total.
    /// </summary>
    public List<UnmappedServiceRow> Services { get; set; } = [];
}

/// <summary>One service's share of the stored unmapped files.</summary>
public sealed class UnmappedServiceRow
{
    public string Service { get; set; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>One row of a service's unmapped file list.</summary>
public sealed class UnmappedFileResponse
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Url { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>Accepted response for an unmapped-cache scan or removal.</summary>
public sealed class UnmappedCacheStartResponse
{
    public Guid OperationId { get; set; }
    public string Message { get; set; } = string.Empty;
    public OperationStatus Status { get; set; } = OperationStatus.Running;
}
