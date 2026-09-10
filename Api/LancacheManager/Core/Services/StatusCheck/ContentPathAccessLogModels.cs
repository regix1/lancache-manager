using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>Short-lived exact access-log candidate. <see cref="Target"/> never enters a wire DTO.</summary>
internal sealed record ContentPathSample(
    string Service,
    string Host,
    string Target,
    DateTimeOffset ObservedAtUtc,
    string CacheOutcome,
    int StatusCode,
    long Bytes);

/// <summary>
/// Aggregated Rust content-scan result across a datasource set, before the security filters and
/// sample selection the check service applies in C#. <see cref="Availability"/> is one of
/// "available", "unreadable", or "logMissing".
/// </summary>
internal sealed record ContentPathRawScan(
    string Availability,
    bool ScanTruncated,
    long ScannedBytes,
    IReadOnlyList<RustContentSample> Records);
