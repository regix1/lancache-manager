using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Evaluates whether a prospective long-running operation conflicts with any
/// currently-active operation in <see cref="IUnifiedOperationTracker"/>.
///
/// Replaces the four ad-hoc conflict-response shapes scattered across controllers
/// (<c>ConflictResponse</c> / <c>ErrorResponse</c> / anonymous <c>{error}</c> /
/// anonymous <c>{error, operationId}</c>) with a single canonical
/// <see cref="OperationConflictResponse"/>.
/// </summary>
public interface IOperationConflictChecker
{
    /// <summary>
    /// Called by controllers just before <c>RegisterOperation</c>. Returns <c>null</c>
    /// if the new op is allowed to proceed; otherwise returns a canonical 409 body
    /// describing the blocking active operation.
    /// </summary>
    Task<OperationConflictResponse?> CheckAsync(OperationType newType, ConflictScope newScope, CancellationToken ct);
}

/// <summary>
/// Concrete checker - implements the Phase 3 overlap matrix from
/// <c>.cursor/plans/game_cache_eviction_cleanup_a228ef88.plan.md</c>.
/// Policy-only: no disk / DB side effects. Uses the tracker as its sole source of state.
/// </summary>
public sealed class OperationConflictChecker : IOperationConflictChecker
{
    private readonly IUnifiedOperationTracker _tracker;
    private readonly ILogger<OperationConflictChecker> _logger;

    public OperationConflictChecker(
        IUnifiedOperationTracker tracker,
        ILogger<OperationConflictChecker> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public Task<OperationConflictResponse?> CheckAsync(OperationType newType, ConflictScope newScope, CancellationToken ct)
    {
        // Snapshot active ops once. Iterate ALL types (pass null) - the matrix spans multiple types per new op.
        var active = _tracker.GetActiveOperations(null);

        foreach (var op in active)
        {
            ct.ThrowIfCancellationRequested();

            var verdict = Evaluate(newType, newScope, op);
            if (verdict != null)
            {
                // A conflict is an expected policy result, and background services may probe this
                // checker frequently while waiting for a heavy operation to finish. Keep the detail
                // available for diagnostics without flooding the normal Information-level console.
                _logger.LogDebug(
                    "Conflict: new {NewType}/{NewScope} blocked by active {ActiveType} (Id={ActiveId}, Scope={ActiveScope}, StageKey={StageKey})",
                    newType, newScope.ToTrackerKey(), op.Type, op.Id, verdict.ActiveOperationScope, verdict.StageKey);
                return Task.FromResult<OperationConflictResponse?>(verdict);
            }
        }

        return Task.FromResult<OperationConflictResponse?>(null);
    }

    /// <summary>
    /// Pure decision function for ONE active op. Returns the 409 body if the new op must be
    /// blocked, or <c>null</c> to let the caller continue checking other active ops.
    /// </summary>
    private static OperationConflictResponse? Evaluate(OperationType newType, ConflictScope newScope, OperationInfo activeOp)
    {
        var activeScope = DeriveScope(activeOp);
        var activeService = ServiceForScope(activeScope);
        var newService = ServiceForScope(newScope);

        // ---- 1. Global-catastrophic: DatabaseReset / CacheClearing ----
        // Any new op is blocked by an active global; a new global blocks any active op.
        if (activeOp.Type == OperationType.DatabaseReset || activeOp.Type == OperationType.CacheClearing)
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.globalOperationActive",
                englishError: $"Cannot start {newType}: a {activeOp.Type} operation is in progress.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString()
                });
        }

        if (newType == OperationType.DatabaseReset || newType == OperationType.CacheClearing)
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.globalOperationActive",
                englishError: $"Cannot start {newType}: another operation ({activeOp.Type}) is still running.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString()
                });
        }

        // Historical evidence purge atomically replaces shared access.log files even for a
        // logical single-service scope. It therefore registers as Bulk and physically conflicts
        // with every access-log writer, another history purge, and eviction scan/removal work.
        // This rule is intentionally symmetric so either operation is queued regardless of which
        // one reached the tracker first.
        if ((newType == OperationType.HistoricalEvidencePurge
                && ConflictsWithHistoricalEvidencePurge(activeOp.Type))
            || (activeOp.Type == OperationType.HistoricalEvidencePurge
                && ConflictsWithHistoricalEvidencePurge(newType)))
        {
            var duplicate = newType == OperationType.HistoricalEvidencePurge
                && activeOp.Type == OperationType.HistoricalEvidencePurge;
            return BuildResponse(activeOp, activeScope,
                stageKey: duplicate ? "errors.conflict.duplicate" : "errors.conflict.heavyOperationActive",
                englishError: duplicate
                    ? "A historical evidence purge is already in progress."
                    : $"Cannot start {newType}: an active {activeOp.Type} operation uses shared access-log evidence.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString()
                });
        }

        // ---- 1a. Heavy data-pipeline ops run ONE at a time ----
        // The full-sweep data operations (log processing, log removal, game detection, the bulk
        // corruption scan, the cache file scan, and the eviction scan) each spawn a Rust worker
        // that hammers the disk and/or the database and streams high-volume SignalR progress.
        // Running several simultaneously races shared files (access.log, cache dirs) and floods
        // slow clients with progress events, so any two heavy ops conflict - the second one parks
        // in the operation queue (purple waiting card) instead of running alongside the first.
        // Per-service corruption "view details" fetches are deliberately NOT heavy: they stay
        // interactive and keep their own rules in section 2 below.
        var newIsHeavy = IsHeavyDataOp(newType, newScope);
        var activeIsHeavy = IsHeavyDataOp(activeOp.Type, activeScope);
        if (newIsHeavy && activeIsHeavy)
        {
            if (newType == activeOp.Type && newScope.Matches(activeScope))
            {
                // Identical request -> "duplicate" so the queue idempotently returns the
                // active op instead of parking a second copy.
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.duplicate",
                    englishError: $"A {newType} operation for the same target is already in progress.",
                    context: new Dictionary<string, object?>
                    {
                        ["activeType"] = activeOp.Type.ToString(),
                        ["serviceName"] = newScope.Kind == "service" ? newScope.Key : null
                    });
            }

            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.heavyOperationActive",
                englishError: $"Cannot start {newType}: a {activeOp.Type} data operation is in progress.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString()
                });
        }

        // ---- 1b. Log-pipeline ops (LogRemoval / LogProcessing) vs removals ----
        // Heavy×heavy pairings (including duplicates) are already handled by section 1a above, so
        // a log op reaching this point faces only the non-heavy ops. Of those, every REMOVAL type
        // (game/service/corruption/eviction) also rewrites access.log to prune the removed
        // entity's lines - the same file the log pipeline reads/rewrites. They used to run
        // "concurrently" here but in reality serialized on an internal lock, leaving the second
        // op stuck at 0% with a running card and no explanation. Conflicting them instead sends
        // the second op through the wait queue (purple waiting card). Read-only ops (detections,
        // per-service corruption details) still never conflict with the log pipeline.
        if (newType == OperationType.LogRemoval || newType == OperationType.LogProcessing)
        {
            if (RewritesAccessLog(activeOp.Type))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.heavyOperationActive",
                    englishError: $"Cannot start {newType}: an active {activeOp.Type} is rewriting the access log.",
                    context: new Dictionary<string, object?>
                    {
                        ["activeType"] = activeOp.Type.ToString()
                    });
            }

            return null;
        }

        // Mirror: a log-pipeline op is active and the NEW op is a removal that would rewrite
        // access.log underneath it → BLOCK (queued). Anything else (scans/detections) → ALLOW.
        if (activeOp.Type == OperationType.LogRemoval || activeOp.Type == OperationType.LogProcessing)
        {
            if (RewritesAccessLog(newType))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.heavyOperationActive",
                    englishError: $"Cannot start {newType}: an active {activeOp.Type} is using the access log.",
                    context: new Dictionary<string, object?>
                    {
                        ["activeType"] = activeOp.Type.ToString()
                    });
            }

            return null;
        }

        // ---- 1c. CacheSizeScan (cache file scan) ----
        // The cache file scan walks every cache file on disk (minutes-long du/find on network
        // filesystems) and then runs a deletion-speed calibration that hammers the cache disk.
        // Cache-mutating ops would race the walker and skew its results, and the eviction scan
        // is a second full-disk walk that should not run concurrently. Read-only detections are
        // tolerated (they were never blocked before and don't mutate cache files).
        // CacheClearing/DatabaseReset pairings are already handled by section 1 above.
        if (newType == OperationType.CacheSizeScan)
        {
            if (activeOp.Type == OperationType.CacheSizeScan)
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.duplicate",
                    englishError: "A cache file scan is already running.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            if (ConflictsWithCacheSizeScan(activeOp.Type))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.overlappingEntity",
                    englishError: $"Cannot start cache file scan: an active {activeOp.Type} is touching the cache.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            return null; // tolerate detections / mappings / imports
        }

        if (activeOp.Type == OperationType.CacheSizeScan)
        {
            if (ConflictsWithCacheSizeScan(newType))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.cacheFileScanActive",
                    englishError: $"Cannot start {newType}: a cache file scan is in progress.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            return null;
        }

        // ---- 2. Scan × Scan / Scan × matching-removal rules ----
        // GameDetection + CorruptionDetection are read-only → tolerate unrelated removals (fall through).
        // Duplicate scans always block.
        if (newType == OperationType.GameDetection && activeOp.Type == OperationType.GameDetection)
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.duplicate",
                englishError: "Game detection is already running.",
                context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
        }

        if (newType == OperationType.CorruptionDetection && activeOp.Type == OperationType.CorruptionDetection)
        {
            // Per-service "view details" fetches only conflict with the bulk scan (which touches
            // every service) or another fetch for the SAME service. Two detail fetches for
            // DIFFERENT services are independent read-only lookups and must not block each other -
            // CacheManagementService already serializes the actual Rust process via its own lock.
            if (activeScope.Kind == "bulk" || newScope.Kind == "bulk" || newScope.Matches(activeScope))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.duplicate",
                    englishError: "Corruption detection is already running.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            return null;
        }

        // GameDetection/CorruptionDetection alongside any removal → ALLOW (read-only tolerate).
        if (newType == OperationType.GameDetection || newType == OperationType.CorruptionDetection)
        {
            return null;
        }

        if (activeOp.Type == OperationType.GameDetection || activeOp.Type == OperationType.CorruptionDetection)
        {
            return null;
        }

        // EvictionScan × EvictionRemoval (either direction) → BLOCK. Scan × unrelated removal → ALLOW.
        if (newType == OperationType.EvictionScan)
        {
            if (activeOp.Type == OperationType.EvictionScan)
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.duplicate",
                    englishError: "Eviction scan is already running.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            if (activeOp.Type == OperationType.EvictionRemoval)
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.overlappingEntity",
                    englishError: "Cannot run eviction scan while an eviction removal is in progress.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            return null; // EvictionScan tolerates unrelated removals
        }

        if (activeOp.Type == OperationType.EvictionScan)
        {
            if (newType == OperationType.EvictionRemoval)
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.overlappingEntity",
                    englishError: "Cannot start eviction removal while an eviction scan is running.",
                    context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
            }

            return null; // Removals tolerate an active eviction scan
        }

        // ---- 3. Entity-level / service-level conflicts between removal types ----
        // By this point both newType and activeOp.Type are one of:
        // GameRemoval, ServiceRemoval, CorruptionRemoval, EvictionRemoval.

        // Same type + same scope → duplicate BLOCK.
        if (newType == activeOp.Type && newScope.Matches(activeScope))
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.duplicate",
                englishError: $"A {newType} operation for the same target is already in progress.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString(),
                    ["serviceName"] = newScope.Kind == "service" ? newScope.Key : null,
                    ["gameName"] = GetGameName(activeOp)
                });
        }

        // Bulk EvictionRemoval is global - covers everything.
        if (activeOp.Type == OperationType.EvictionRemoval && activeScope.Kind == "bulk")
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.bulkActive",
                englishError: "A bulk eviction removal is in progress.",
                context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
        }

        if (newType == OperationType.EvictionRemoval && newScope.Kind == "bulk")
        {
            // Any active removal blocks a new bulk eviction (bulk needs exclusive eviction write).
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.overlappingEntity",
                englishError: $"Cannot start bulk eviction removal: an active {activeOp.Type} is still running.",
                context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
        }

        // Same type + different scope (both entity-scoped) → ALLOW.
        // Covered types where same-type-different-scope is disjoint:
        //   GameRemoval vs GameRemoval (different entities)
        //   ServiceRemoval vs ServiceRemoval (different services)
        //   CorruptionRemoval vs CorruptionRemoval (different services)
        //   EvictionRemoval vs EvictionRemoval (different per-entity scopes)
        // The cross-type service-covers logic below handles supersets.

        // Service-scoped cross-type overlap on the SAME service → BLOCK.
        // This catches the service-wide pairs the entity-cover logic below does not:
        //   ServiceRemoval  vs CorruptionRemoval
        //   ServiceRemoval  vs EvictionRemoval(scope=service)
        //   CorruptionRemoval vs EvictionRemoval(scope=service)
        if (newScope.Kind == "service" && activeScope.Kind == "service" && newScope.Matches(activeScope))
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.serviceWideActive",
                englishError: $"A service-wide {activeOp.Type} for '{activeScope.Key}' is in progress.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString(),
                    ["serviceName"] = activeScope.Key
                });
        }

        // Service-level covers entity-level (same service on both sides).
        // ServiceRemoval/EvictionRemoval(service)/CorruptionRemoval for service S
        // cover GameRemoval/EvictionRemoval(entity) targeting that same service.
        if (activeScope.Kind == "service" && activeScope.Covers(newScope, newService))
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.serviceWideActive",
                englishError: $"A service-wide {activeOp.Type} for '{activeScope.Key}' is in progress.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString(),
                    ["serviceName"] = activeScope.Key
                });
        }

        // Reverse: new service-scoped removal covers an active entity-level op in the same service.
        if (newScope.Kind == "service" && newScope.Covers(activeScope, activeService))
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.overlappingEntity",
                englishError: $"Cannot start service-wide {newType} for '{newScope.Key}': an active {activeOp.Type} targets the same service.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString(),
                    ["serviceName"] = newScope.Key
                });
        }

        // EvictionRemoval service-wide covers GameRemoval entity belonging to that service (and vice versa).
        // Same-service service-scoped cross-type conflicts are handled by the explicit service/service
        // block above; Covers() is only for service ↔ entity relationships.

        // GameRemoval steam:X ↔ EvictionRemoval scope=steam key=X (same entity) is already caught
        // by the same-type-same-scope check IF we compare scopes. But here the TYPES differ - we
        // need to match scopes across GameRemoval and EvictionRemoval when both are entity-scoped.
        if (IsEntityScoped(newScope) && IsEntityScoped(activeScope) && newScope.Matches(activeScope))
        {
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.overlappingEntity",
                englishError: $"Cannot start {newType}: an active {activeOp.Type} targets the same entity.",
                context: new Dictionary<string, object?>
                {
                    ["activeType"] = activeOp.Type.ToString(),
                    ["gameName"] = GetGameName(activeOp)
                });
        }

        // CorruptionRemoval × GameRemoval / ServiceRemoval on same service → BLOCK.
        // Different services → ALLOW (fixes over-broad lock bug).
        if (newType == OperationType.CorruptionRemoval && activeOp.Type == OperationType.GameRemoval)
        {
            // CorruptionRemoval is service-scoped; GameRemoval's service derived from kind.
            if (newScope.Kind == "service" && activeService != null &&
                string.Equals(newScope.Key, activeService, StringComparison.OrdinalIgnoreCase))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.overlappingEntity",
                    englishError: $"Cannot start corruption removal for '{newScope.Key}': a game removal in that service is in progress.",
                    context: new Dictionary<string, object?>
                    {
                        ["activeType"] = activeOp.Type.ToString(),
                        ["serviceName"] = newScope.Key
                    });
            }
        }

        if (newType == OperationType.GameRemoval && activeOp.Type == OperationType.CorruptionRemoval)
        {
            if (activeScope.Kind == "service" && newService != null &&
                string.Equals(activeScope.Key, newService, StringComparison.OrdinalIgnoreCase))
            {
                return BuildResponse(activeOp, activeScope,
                    stageKey: "errors.conflict.serviceWideActive",
                    englishError: $"A corruption removal is in progress for service '{activeScope.Key}'.",
                    context: new Dictionary<string, object?>
                    {
                        ["activeType"] = activeOp.Type.ToString(),
                        ["serviceName"] = activeScope.Key
                    });
            }
        }

        // All other combinations → ALLOW (disjoint).
        return null;
    }

    /// <summary>
    /// Derives a <see cref="ConflictScope"/> from an active op's metadata.
    /// Handles both <see cref="RemovalMetrics"/> (GameRemoval/ServiceRemoval/CorruptionRemoval)
    /// and <see cref="EvictionRemovalMetadata"/> (EvictionRemoval). Falls back to
    /// <see cref="ConflictScope.Bulk"/> for globals / unknown metadata.
    /// </summary>
    private static ConflictScope DeriveScope(OperationInfo op)
    {
        switch (op.Metadata)
        {
            case RemovalMetrics m when !string.IsNullOrEmpty(m.EntityKey):
            {
                var kind = string.IsNullOrEmpty(m.EntityKind)
                    ? KindForType(op.Type)
                    : m.EntityKind!;
                return new ConflictScope(kind, m.EntityKey);
            }

            case EvictionRemovalMetadata e:
            {
                if (string.IsNullOrEmpty(e.Scope) || string.IsNullOrEmpty(e.Key))
                {
                    return ConflictScope.Bulk();
                }
                return new ConflictScope(e.Scope!, e.Key!);
            }

            // Logical service scope is retained in this metadata for recovery display only.
            // Physically every history purge replaces shared access-log files.
            case HistoricalEvidencePurgeMetadata:
                return ConflictScope.Bulk();

            // Per-service "Corruption Details (service)" fetches carry ServiceName; the bulk
            // scan's metadata leaves it null, so it correctly falls through to Bulk() below.
            case CorruptionDetectionMetrics c when !string.IsNullOrEmpty(c.ServiceName):
                return ConflictScope.Service(c.ServiceName!);

            default:
                // No metadata (global scans / detections / cache-clear / db-reset) - treat as bulk.
                return ConflictScope.Bulk();
        }
    }

    /// <summary>
    /// Kind fallback for a RemovalMetrics with a missing EntityKind.
    /// ServiceRemoval + CorruptionRemoval both store the service name in EntityKey,
    /// so they fall under "service".
    /// </summary>
    private static string KindForType(OperationType type) => type switch
    {
        OperationType.ServiceRemoval => "service",
        OperationType.CorruptionRemoval => "service",
        OperationType.GameRemoval => "steam", // legacy rows pre-EntityKind default to steam (matches CacheController fallback)
        _ => "bulk"
    };

    /// <summary>
    /// Maps a game scope → the service name it belongs to, for the <c>Covers</c> check.
    /// ServiceRemoval svc="steam" covers GameRemoval steam:480 (service = "steam").
    /// ServiceRemoval svc="epicgames" covers GameRemoval epic:fn (service = "epicgames").
    /// Named (Blizzard/Riot) games encode their service in the Key prefix
    /// (<c>"{service}:{gameName}"</c>), so a ServiceRemoval svc="blizzard" covers
    /// a named GameRemoval named:"blizzard:Diablo".
    /// </summary>
    private static string? ServiceForScope(ConflictScope scope) => scope.Kind switch
    {
        "steam" => "steam",
        "epic" => "epicgames",
        "named" => NamedScopeService(scope.Key),
        "service" => null,   // a service scope IS a service, not a member of one
        "bulk" => null,
        _ => null
    };

    /// <summary>
    /// Extracts the lowercased service name from a named scope Key (<c>"{service}:{gameName}"</c>).
    /// The service is the prefix up to the FIRST ':' (gameName may itself contain ':').
    /// </summary>
    private static string? NamedScopeService(string key)
    {
        var idx = key.IndexOf(':');
        return idx <= 0 ? null : key[..idx];
    }

    private static bool IsEntityScoped(ConflictScope scope) =>
        scope.Kind == "steam" || scope.Kind == "epic" || scope.Kind == "named";

    /// <summary>
    /// Removal types whose Rust workers ALSO rewrite access.log (pruning the removed entity's
    /// log lines), so they may not overlap the log pipeline (section 1b). Kept separate from
    /// IsHeavyDataOp: these stay concurrent with each other per the entity/service scope rules.
    /// </summary>
    private static bool RewritesAccessLog(OperationType type) =>
        type is OperationType.GameRemoval
            or OperationType.ServiceRemoval
            or OperationType.CorruptionRemoval
            or OperationType.EvictionRemoval
            or OperationType.HistoricalEvidencePurge;

    private static bool ConflictsWithHistoricalEvidencePurge(OperationType type) =>
        RewritesAccessLog(type) || type == OperationType.EvictionScan;

    /// <summary>
    /// Full-sweep data operations that must run one at a time (section 1a). Each spawns a
    /// Rust worker over the whole log file / cache tree. CorruptionDetection counts only as
    /// its BULK scan; per-service detail fetches remain lightweight interactive reads.
    /// DatabaseReset/CacheClearing are excluded only because section 1 blocks them earlier.
    /// </summary>
    private static bool IsHeavyDataOp(OperationType type, ConflictScope scope) => type switch
    {
        OperationType.LogProcessing => true,
        OperationType.LogRemoval => true,
        OperationType.GameDetection => true,
        OperationType.CacheSizeScan => true,
        OperationType.EvictionScan => true,
        OperationType.CorruptionDetection => scope.Kind == "bulk",
        _ => false
    };

    /// <summary>
    /// Operation types that may not overlap a CacheSizeScan (in either direction):
    /// everything that deletes cache files plus the eviction scan's full-disk walk.
    /// CacheClearing/DatabaseReset are excluded only because section 1 blocks them earlier.
    /// </summary>
    private static bool ConflictsWithCacheSizeScan(OperationType type) =>
        type is OperationType.GameRemoval
            or OperationType.ServiceRemoval
            or OperationType.CorruptionRemoval
            or OperationType.EvictionRemoval
            or OperationType.EvictionScan;

    private static string? GetGameName(OperationInfo op) => op.Metadata switch
    {
        RemovalMetrics m => m.EntityName,
        EvictionRemovalMetadata e => e.GameName,
        _ => null
    };

    private static OperationConflictResponse BuildResponse(
        OperationInfo activeOp,
        ConflictScope activeScope,
        string stageKey,
        string englishError,
        Dictionary<string, object?>? context)
    {
        return new OperationConflictResponse
        {
            Code = "OPERATION_CONFLICT",
            StageKey = stageKey,
            Error = englishError,
            ActiveOperationId = activeOp.Id,
            ActiveOperationType = activeOp.Type.ToString(),
            ActiveOperationScope = activeScope.Kind == "bulk" ? "bulk" : activeScope.ToTrackerKey(),
            Context = context
        };
    }
}
