namespace LancacheManager.Models.Responses;

/// <summary>
/// Result of resolving a single domain (or a wildcard entry's literal probe substitute) during a
/// Status Check sweep or an ad hoc test-a-domain request.
/// </summary>
public class DomainCheckResult
{
    /// <summary>The hostname actually queried. Wildcard entries (<c>*.x.y</c>) probe with the
    /// literal label "status-check" substituted for the leading <c>*</c> (e.g. status-check.x.y).</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The raw list entry as it appears in the domain file (display this, not <see cref="Domain"/>).</summary>
    public string OriginalEntry { get; set; } = string.Empty;

    public string Service { get; set; } = string.Empty;

    /// <summary>"resolved" | "mismatched" | "unresolved" | "unverified" | "blocked". Semantics per
    /// contract amendment v1.4 (active verification): resolved = heartbeat-verified live OR matches
    /// the expected cache IPs; mismatched = no heartbeat, no expected match, answers are public IPs
    /// (traffic is going to the internet); unverified = no heartbeat, no expected match, answers
    /// are private IPs (cache down or wrong host - can't tell); unresolved = no A records;
    /// blocked (v1.5) = every answer is a deliberate blackhole (0.0.0.0/::), typically an
    /// intentionally blocked telemetry endpoint - benign, never counted as a failure.</summary>
    public string Status { get; set; } = string.Empty;

    public List<string> ResolvedIps { get; set; } = new();

    public List<string> ExpectedIps { get; set; } = new();

    /// <summary>True when one of the resolved IPs answered <c>/lancache-heartbeat</c> with the
    /// X-LanCache-Processed-By header during this sweep (contract amendment v1.4 - active,
    /// fork/image-agnostic verification).</summary>
    public bool HeartbeatVerified { get; set; }

    /// <summary>Hostname from the verifying IP's X-LanCache-Processed-By header; null when not
    /// heartbeat-verified.</summary>
    public string? ServedBy { get; set; }

    public string? Error { get; set; }

    public double? LatencyMs { get; set; }

    /// <summary>Public-edge HTTP/HTTPS probe of this hostname (v1.5), run for every swept and
    /// ad hoc tested domain via the content lane's shared pipeline. Null on results persisted
    /// before v1.5 and when the probe crashed unexpectedly.</summary>
    public HostProtocolProbeResult? EdgeProbe { get; set; }
}

/// <summary>Aggregated verdict for one cache-domains service (e.g. "steam").</summary>
public class ServiceCheckResult
{
    public string Service { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>"resolved" | "partial" | "unresolved" | "disabled" (contract amendment v1.1 -
    /// DISABLE_&lt;SERVICE&gt;=true in lancache-dns; domains are never queried, resolvedCount=0,
    /// totalCount=listed domain count) | "unverified" (v1.3/v1.4 - no domain could be confirmed
    /// but at least one resolves to a private IP that isn't answering as lancache).</summary>
    public string Status { get; set; } = string.Empty;

    public int ResolvedCount { get; set; }
    public int TotalCount { get; set; }
    public List<DomainCheckResult> Domains { get; set; } = new();
}

/// <summary>Result of probing the lancache server's <c>/lancache-heartbeat</c> endpoint.</summary>
public class HeartbeatResult
{
    public bool Reachable { get; set; }
    public string? ServedBy { get; set; }
    public string? CacheIp { get; set; }
    public string? Error { get; set; }
}

public class StatusCheckSummary
{
    public int TotalServices { get; set; }
    public int ResolvedServices { get; set; }
    public int PartialServices { get; set; }
    public int UnresolvedServices { get; set; }

    /// <summary>Count of services with status "disabled" (DISABLE_&lt;SERVICE&gt;=true in lancache-dns) -
    /// contract amendment v1.1. Excluded from resolved/partial/unresolved counts.</summary>
    public int DisabledServices { get; set; }

    /// <summary>Count of services with status "unverified" (contract amendment v1.3 - resolving,
    /// but no expected cache IP was determinable). Excluded from resolved/partial/unresolved, so
    /// Resolved + Partial + Unresolved + Disabled + Unverified == TotalServices always holds.</summary>
    public int UnverifiedServices { get; set; }

    public int TotalDomains { get; set; }
    public int ResolvedDomains { get; set; }

    /// <summary>Count of domains with status "unverified" across all services (contract amendment v1.3).</summary>
    public int UnverifiedDomains { get; set; }
}

/// <summary>Full result of one Status Check sweep. Persisted via <c>IStateService</c> so it survives restarts.</summary>
public class StatusCheckResult
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }

    /// <summary>"configured" | "detected" | "system" — which DNS resolver answered the sweep's queries.</summary>
    public string ResolverSource { get; set; } = string.Empty;

    public string? DnsServer { get; set; }

    public List<string> ExpectedCacheIps { get; set; } = new();

    /// <summary>"config" | "dns" | "dockerInspect" | "envFile" | "detected" | "none" — where
    /// <see cref="ExpectedCacheIps"/> came from (contract amendments v1.1/v1.2). "none" is an
    /// additive honest state for "nothing determined the cache IP", not part of the original
    /// frozen enum but harmless for consumers that only switch on the known values. "dns" is the
    /// heartbeat-verified answer the detected lancache DNS advertises for the test domain.</summary>
    public string ExpectedIpSource { get; set; } = string.Empty;

    public HeartbeatResult Heartbeat { get; set; } = new();

    public List<ServiceCheckResult> Services { get; set; } = new();

    public StatusCheckSummary Summary { get; set; } = new();

    /// <summary>Mean <see cref="DomainCheckResult.LatencyMs"/> across every domain result in the
    /// sweep that has one, regardless of status; null when nothing measured a latency. Older
    /// persisted results deserialize this as null automatically.</summary>
    public double? AvgLatencyMs { get; set; }

    /// <summary>Verified cache IPs grouped by the hostname that answered their heartbeat during
    /// this sweep; empty when nothing heartbeat-verified. Older persisted results deserialize this
    /// as an empty list automatically.</summary>
    public List<CacheNodeInfo> CacheNodes { get; set; } = new();

    /// <summary>Empirical cache traversal and current public-edge protocol observations for recent,
    /// concrete content paths. Default-initialized so older persisted snapshots remain readable.</summary>
    public StatusCheckContentReport ContentReport { get; set; } = new();
}

/// <summary>Bounded report built from recent real access-log paths during an explicit Status Check.</summary>
public class StatusCheckContentReport
{
    /// <summary>"available" | "noSamples" | "unreadable" | "logMissing".</summary>
    public string Availability { get; set; } = "logMissing";
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public bool ScanTruncated { get; set; }
    public long ScannedBytes { get; set; }
    public List<ContentPathCheckResult> Paths { get; set; } = new();
}

/// <summary>Evidence and current public-edge observations for one concrete service/host/path.</summary>
public class ContentPathCheckResult
{
    public string Service { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string PathDisplay { get; set; } = string.Empty;
    public DateTimeOffset? SampleObservedAtUtc { get; set; }
    public CacheTraversalEvidence? CacheEvidence { get; set; }

    /// <summary>"notRun" | "bothUsable" | "httpUsable" | "httpsOnlyCandidate" | "inconclusive".</summary>
    public string ProtocolStatus { get; set; } = "notRun";

    /// <summary>Bounded lower-camel reason key; never a raw network or exception message.</summary>
    public string? ProtocolReason { get; set; }
    public int ConsensusEdges { get; set; }
    public int TotalPublicEdges { get; set; }
    public List<ContentPathEdgeResult> Edges { get; set; } = new();
}

/// <summary>Authoritative positive-byte cache observation for the exact sampled request target.</summary>
public class CacheTraversalEvidence
{
    /// <summary>"hit" | "miss".</summary>
    public string Outcome { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long Bytes { get; set; }
}

/// <summary>HTTP and HTTPS results from one explicitly pinned public edge address.</summary>
public class ContentPathEdgeResult
{
    public string Address { get; set; } = string.Empty;

    /// <summary>"ipv4" | "ipv6".</summary>
    public string AddressFamily { get; set; } = string.Empty;
    public ProtocolProbeResult Http { get; set; } = new();
    public ProtocolProbeResult Https { get; set; } = new();
}

/// <summary>Bounded transport outcome; contains no response body or raw exception detail.</summary>
public class ProtocolProbeResult
{
    /// <summary>"content" | "redirectToHttps" | "otherRedirect" | "denied" |
    /// "notFoundOrStale" | "rangeRejected" | "serverError" | "tlsCertificateFailure" |
    /// "connectFailure" | "timeout" | "invalidResponse".</summary>
    public string Outcome { get; set; } = "invalidResponse";
    public int? StatusCode { get; set; }
    public string? RedirectScheme { get; set; }
}

/// <summary>Current public-edge HTTP/HTTPS behaviour of one ad hoc tested hostname (v1.5),
/// produced by the same resolve-and-probe pipeline as the sweep's content lane.</summary>
public class HostProtocolProbeResult
{
    /// <summary>Same vocabulary as <see cref="ContentPathCheckResult.ProtocolStatus"/>.</summary>
    public string ProtocolStatus { get; set; } = "notRun";
    public string? ProtocolReason { get; set; }
    public int ConsensusEdges { get; set; }
    public int TotalPublicEdges { get; set; }
    public List<ContentPathEdgeResult> Edges { get; set; } = new();
}

/// <summary>One cache node behind the resolved fleet, identified by its
/// <c>X-LanCache-Processed-By</c> hostname, with every verified IP that answered as it.</summary>
public class CacheNodeInfo
{
    public string ServedBy { get; set; } = string.Empty;
    public List<string> Ips { get; set; } = new();
}

/// <summary>Where the cache-domains list currently in memory/on disk came from.</summary>
public class DomainsSource
{
    public string RepoUrl { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;

    /// <summary>Null when <see cref="EnvSource"/> is "dockerInspect" (contract amendment v1.2).</summary>
    public string? EnvFilePath { get; set; }
    public bool NoFetch { get; set; }
    public DateTime? FetchedAtUtc { get; set; }
    public bool FromCache { get; set; }

    /// <summary>"dockerInspect" | "envFile" | "defaults" — which tier supplied
    /// CACHE_DOMAINS_REPO/BRANCH/NOFETCH (contract amendment v1.2).</summary>
    public string EnvSource { get; set; } = "defaults";

    /// <summary>Additive diagnostic field (not part of the frozen wire contract's minimum shape) -
    /// non-null when the most recent load hit a fetch failure or an unresolvable NOFETCH-with-no-disk-copy
    /// state. Frontend consumers may ignore it.</summary>
    public string? Error { get; set; }
}

/// <summary>A single cache-domains service entry with its resolved domain strings.</summary>
public class CacheDomainService
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Domains { get; set; } = new();
}

/// <summary>In-memory/on-disk representation of the fetched cache-domains list.</summary>
public class CacheDomainsList
{
    public List<CacheDomainService> Services { get; set; } = new();
}

/// <summary>Outcome of an explicit (user-triggered) domains-list refresh request.</summary>
public class CacheDomainsRefreshOutcome
{
    public bool Success { get; init; }

    /// <summary>Set when <see cref="Success"/> is false because NOFETCH is enabled - the controller
    /// surfaces this as a 409 with this message.</summary>
    public string? BlockedReason { get; init; }

    public CacheDomainsList Domains { get; init; } = new();
    public DomainsSource Source { get; init; } = new();
}

// ===== Controller response/request shapes (frozen contract) =====

public class StatusCheckStateResponse
{
    public StatusCheckResult? LastResult { get; set; }
    public DomainsSource? DomainsSource { get; set; }
    public bool IsRunning { get; set; }
    public Guid? OperationId { get; set; }

    /// <summary>The persisted DNS resolver mode ("auto" | "bridge" | "host"). Default "auto".</summary>
    public string ResolverMode { get; set; } = "auto";
}

public class RunStatusCheckResponse
{
    public Guid OperationId { get; set; }
}

public class TestDomainRequest
{
    public string Domain { get; set; } = string.Empty;
}

public class TestDomainResponse
{
    /// <summary>Carries the public-edge probe in <see cref="DomainCheckResult.EdgeProbe"/>.</summary>
    public DomainCheckResult Result { get; set; } = new();
    public HeartbeatResult? Heartbeat { get; set; }
}

public class RefreshDomainsResponse
{
    public DomainsSource DomainsSource { get; set; } = new();
    public int ServiceCount { get; set; }
    public int DomainCount { get; set; }
}

/// <summary>Body of POST api/status-check/resolver-mode: the DNS resolver mode to persist
/// ("auto" | "bridge" | "host").</summary>
public class SetResolverModeRequest
{
    public string Mode { get; set; } = string.Empty;
}

/// <summary>Response for POST api/status-check/resolver-mode: the mode that was persisted.</summary>
public class SetResolverModeResponse
{
    public string ResolverMode { get; set; } = string.Empty;
}

public class GetDomainsResponse
{
    public List<CacheDomainService> Services { get; set; } = new();
}
