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
/// Concrete checker — implements the Phase 3 overlap matrix from
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
        // Snapshot active ops once. Iterate ALL types (pass null) — the matrix spans multiple types per new op.
        var active = _tracker.GetActiveOperations(null);

        foreach (var op in active)
        {
            ct.ThrowIfCancellationRequested();

            var verdict = Evaluate(newType, newScope, op);
            if (verdict != null)
            {
                _logger.LogInformation(
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
        var activeService = ServiceForKind(activeScope.Kind);
        var newService = ServiceForKind(newScope.Kind);

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
            return BuildResponse(activeOp, activeScope,
                stageKey: "errors.conflict.duplicate",
                englishError: "Corruption detection is already running.",
                context: new Dictionary<string, object?> { ["activeType"] = activeOp.Type.ToString() });
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

        // Bulk EvictionRemoval is global — covers everything.
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
        // by the same-type-same-scope check IF we compare scopes. But here the TYPES differ — we
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

            default:
                // No metadata (global scans / detections / cache-clear / db-reset) — treat as bulk.
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
    /// Hardcoded mapping from game kind → service name for the <c>Covers</c> check.
    /// ServiceRemoval svc="steam" covers GameRemoval steam:480 (service = "steam").
    /// ServiceRemoval svc="epicgames" covers GameRemoval epic:fn (service = "epicgames").
    /// </summary>
    private static string? ServiceForKind(string kind) => kind switch
    {
        "steam" => "steam",
        "epic" => "epicgames",
        "service" => null,   // a service scope IS a service, not a member of one
        "bulk" => null,
        _ => null
    };

    private static bool IsEntityScoped(ConflictScope scope) =>
        scope.Kind == "steam" || scope.Kind == "epic";

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
