---
name: Operation conflict + shared-runner adoption gaps
overview: Bring controllers and services outside the original game_cache_eviction_cleanup plan scope onto the same conflict-checker, operation-tracker, and data-service primitives introduced in that plan. Audit (W10/W11 swarm 20260421-152100) found 3 controller-level adoption gaps and 1 service-level bypass that matter; this plan closes them without over-reaching into patterns that are intentionally different.
todos:
  - id: data-migration-checker
    content: "DataMigrationController: replace manual `GetActiveOperations(DataImport).Any()` TOCTOU check with `IOperationConflictChecker.CheckAsync`; return `OperationConflictResponse` on 409; stop returning `ErrorResponse` shape"
    status: pending
  - id: logs-controller-checker
    content: "LogsController: inject `IOperationConflictChecker`, wrap the bare `_ = Task.Run(...)` at ~line 324 in a tracked operation with proper registration + completion; replace 6 ad-hoc 409 shapes with `OperationConflictResponse`"
    status: pending
  - id: epic-mapping-controller
    content: "EpicGameMappingController: use injected tracker in `StartRefresh` (return 409 on double-start instead of 200 with `started:false`); gate `ResolveDownloadsAsync` via the checker"
    status: pending
  - id: epic-mapping-service-atomic
    content: "`EpicMappingService._isRunning` — swap plain `bool` for `Interlocked.Exchange<int>`-style atomic gate (matches CacheReconciliationService pattern)"
    status: pending
  - id: unevict-delegate
    content: "`CacheReconciliationService.UnevictCachedGameDetectionsAsync` (lines ~1073-1141) — delegate reads/writes on `CachedGameDetections` through `GameCacheDetectionDataService` and the eviction-flag flip through `EvictedDetectionPreservationService` (or a sibling `UnpreserveAsync` method); this is the inverse of the already-delegated evict path"
    status: pending
  - id: game-images-interlocked
    content: "`GameImagesController._cacheGeneration` — convert plain static `long` assignment to `Interlocked.Exchange` (atomic correctness, not just atomic-on-64-bit-luck)"
    status: pending
  - id: verify-build
    content: "Run dotnet build + tests, Web tsc/lint/knip/vite, cargo check; smoke-test the 3 affected endpoints (data migration start/conflict, log processing start/conflict, Epic mapping refresh start/conflict)"
    status: pending
isProject: false
---

# Operation conflict + shared-runner adoption gaps

## Context

Plan `.cursor/plans/game_cache_eviction_cleanup_a228ef88.plan.md` introduced:
- `IOperationConflictChecker` + `ConflictScope` + `OperationConflictResponse`
- `TrackedRemovalOperationRunner`
- `GameCacheDetectionDataService`, `UnknownGameResolutionService`, `EvictedDetectionPreservationService`

Adoption landed cleanly in the plan-named controllers (`GamesController`, `CacheController`, `StatsController`, `DatabaseController`) and services (`CacheReconciliationService` evict path). Swarm audit `20260421-152100-1470826899` (Workers 10 + 11) found that four adjacent controllers/services do the same kind of work but still use pre-plan primitives. This plan closes those gaps without disrupting patterns that are intentionally different.

## Scope

### In scope — adoption gaps worth closing
1. `Api/LancacheManager/Controllers/DataMigrationController.cs` — manual conflict check, wrong 409 shape.
2. `Api/LancacheManager/Controllers/LogsController.cs` — no checker, no tracker, fire-and-forget, 6 ad-hoc 409 shapes.
3. `Api/LancacheManager/Controllers/EpicGameMappingController.cs` — tracker injected but never called.
4. `Api/LancacheManager/Services/EpicMappingService.cs` — non-atomic `_isRunning` bool.
5. `Api/LancacheManager/Core/Services/CacheReconciliationService.cs` `UnevictCachedGameDetectionsAsync` (~lines 1073-1141) — inverse of the evict path but bypasses the extracted services.
6. `Api/LancacheManager/Controllers/GameImagesController.cs` `_cacheGeneration` — static `long` written with plain assignment.

### Out of scope — intentionally different patterns

Swarm W11 flagged several bypass sites that should NOT be forced onto the new primitives:

| Site | Why it stays different |
|------|-----------------------|
| `CacheReconciliationService` eviction path hand-rolling started/progress/complete | Background-service-triggered (not user-triggered). `TrackedRemovalOperationRunner` assumes a user action and controller HTTP lifecycle. Forcing it here would add indirection without benefit. |
| `CacheManagementService:1231/1472`, `CacheClearingService:558-559`, `CacheController:1390/1403`, `DatabaseService:689` direct `CachedGameDetections`/`CachedServiceDetections` access | These are whole-table clear/delete operations, not the upsert/load/save pattern `GameCacheDetectionDataService` owns. Indirection without benefit. |
| `SteamKit2Service.Mapping.cs:119` `ResolveOrphanDepotsAsync` | Serves PICS-time depot mapping (different concern from runtime unknown-depot detection tracking). Naming overlap is confusing but the logic is legitimately separate. |

Document these decisions inline in the code comments where the duplication is visible so future audits understand the split.

## Phases

### Phase 1: Controller conflict-checker adoption (parallel-safe)

Three controllers, all independent — can be done in any order.

#### 1.1 DataMigrationController (MEDIUM — TOCTOU + wrong shape)

Current state:
```csharp
if (_operationTracker.GetActiveOperations(OperationType.DataImport).Any())
{
    return Conflict(new ErrorResponse { ... });
}
```

Target state: inject `IOperationConflictChecker`, call `CheckAsync(ConflictScope.Bulk(...), OperationType.DataImport, ...)`; on conflict return `Conflict(conflictResponse)`.

Exit criteria:
- Zero `GetActiveOperations(...).Any()` race checks remain in this controller.
- 409 responses use `OperationConflictResponse` (so frontend `handleCancel` works).
- No new `ErrorResponse`-shape 409 paths introduced.

#### 1.2 LogsController (MEDIUM — fully ungated + 6 ad-hoc 409 shapes)

Current state (line ~324):
```csharp
_ = Task.Run(async () => { /* log processing */ });
return Ok(new { started = true });
```

Target state:
- Inject `IOperationConflictChecker` and `IUnifiedOperationTracker`.
- Before `Task.Run`, call `CheckAsync(ConflictScope.Bulk("log-processing"), OperationType.LogProcessing, ...)` (add `OperationType.LogProcessing` enum value if missing).
- Register a tracker operation, emit started SignalR, fire-and-forget the work inside the registered operation, mark complete/failed on exit.
- Replace the 6 `ConflictResponse`/`ErrorResponse` 409 shapes in this file with `OperationConflictResponse`.

Exit criteria:
- Every long-running action in this controller goes through checker → tracker → work → complete.
- Zero ad-hoc 409 shapes remain; every 409 uses `OperationConflictResponse`.
- `_ = Task.Run(...)` at line 324 is replaced by a proper tracked operation (orphaned fire-and-forget is gone).

#### 1.3 EpicGameMappingController (LOW — injected tracker unused)

Current state:
- `StartRefresh` returns `Ok(new { started = false })` when already running — unusual choice that hides conflicts from the frontend.
- `ResolveDownloadsAsync` has no guard at all.

Target state:
- `StartRefresh` uses `IOperationConflictChecker.CheckAsync(ConflictScope.Bulk("epic-mapping"), OperationType.EpicMappingRefresh, ...)`; returns 409 `OperationConflictResponse` on conflict.
- `ResolveDownloadsAsync` gated the same way.
- Tracker operations registered for both endpoints so the frontend can observe and cancel.

Exit criteria:
- Both endpoints emit 409 (not 200) on double-start.
- Both endpoints register a tracker operation that the active-removals/active-operations endpoint can surface.

### Phase 2: Service-layer adoption

#### 2.1 EpicMappingService._isRunning atomic

Current state:
```csharp
private bool _isRunning;
// ... if (_isRunning) return; _isRunning = true; ... _isRunning = false;
```

Target state:
```csharp
private int _isRunning;
// ... if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0) return; try { ... } finally { Volatile.Write(ref _isRunning, 0); }
```

Exit criteria:
- Compare-and-swap matches the pattern used by `CacheReconciliationService` single-flight guards.
- No plain `_isRunning = true; ... _isRunning = false;` assignments remain.

#### 2.2 UnevictCachedGameDetectionsAsync delegation

Current state: `CacheReconciliationService.UnevictCachedGameDetectionsAsync` (lines ~1073-1141) loads, updates, and saves `CachedGameDetections` directly via a scoped `DbContext`. The sibling evict path delegates to `EvictedDetectionPreservationService` (landed in Phase 5).

Decision point during implementation: extend `EvictedDetectionPreservationService` with an `UnpreserveAsync(...)` method (parallel to the existing `PreserveAsync`), OR extend `GameCacheDetectionDataService` with a dedicated "mark un-evicted" update method. Prefer the first (preservation service owns the IsEvicted flag's write side).

Target state:
- The un-evict path delegates its `CachedGameDetections` reads through `GameCacheDetectionDataService` and its `IsEvicted = false` bulk flip through `EvictedDetectionPreservationService.UnpreserveAsync(...)` (new method).
- Transaction handling stays inside `CreateExecutionStrategy().ExecuteAsync(...)` (per memory `ef_transaction_retry_strategy.md`).

Exit criteria:
- Zero direct `context.CachedGameDetections` mutations in `UnevictCachedGameDetectionsAsync`.
- Evict path and un-evict path are structurally symmetric.

#### 2.3 GameImagesController._cacheGeneration Interlocked

Current state:
```csharp
private static long _cacheGeneration;
// ... _cacheGeneration = newValue;
```

Target state:
```csharp
private static long _cacheGeneration;
// ... Interlocked.Exchange(ref _cacheGeneration, newValue);
// reads: Interlocked.Read(ref _cacheGeneration)
```

Exit criteria:
- Every write uses `Interlocked.Exchange` or `Interlocked.Increment`.
- Every read uses `Interlocked.Read` (or is provably read-once on a single thread).

### Phase 3: Inline scope documentation

Add one-line comments at each of the five out-of-scope bypass sites (listed in Scope table above) explaining WHY the direct path is deliberate, so the next audit doesn't re-flag them:

```csharp
// Direct DbContext access: whole-table clear, not the upsert pattern GameCacheDetectionDataService owns.
```

Exit criteria:
- Each out-of-scope bypass has a comment naming the primitive it deliberately skips and why.

### Phase 4: Verification

1. `dotnet build lancache-manager.sln --no-restore -nologo`
2. `dotnet test Tests/LancacheManager.Tests/LancacheManager.Tests.csproj --no-build -nologo`
3. `cd Web && npx tsc --noEmit --pretty false && npm run lint -- --max-warnings=0 && npm run knip && npx vite build`
4. `cd rust-processor && cargo check --bins --quiet`
5. Manual smoke (if local PostgreSQL/Docker available):
   - Start data migration twice → second call returns 409 with `OperationConflictResponse` shape, frontend cancel works.
   - Start log processing twice → same.
   - Start Epic mapping refresh twice → same (now 409 instead of 200).
   - Evict then un-evict a single entity → both paths use the shared services, detection row round-trips correctly.

## Dependency order

```
  [1.1] DataMigrationController    ──┐
  [1.2] LogsController              ──┤  parallel, all independent
  [1.3] EpicGameMappingController  ──┤
  [2.1] EpicMappingService atomic   ──┘
                                        ↓
  [2.2] UnevictCachedGameDetections (depends on new UnpreserveAsync method in preservation service)
  [2.3] GameImagesController Interlocked (standalone, can go anywhere)
                                        ↓
  [3]   Inline scope comments (after 1+2 to reflect final state)
                                        ↓
  [4]   Verification
```

## Risks

- **New `OperationType.LogProcessing` / `OperationType.EpicMappingRefresh` enum values** — if these already exist, reuse them. If not, adding them is a trivial enum extension but audit the frontend to see whether the tracker/active-ops display needs string localization for the new types.
- **`EvictedDetectionPreservationService.UnpreserveAsync(...)`** is a new public API. Decide whether it belongs there or on `GameCacheDetectionDataService`. The un-evict path is the inverse of the preserve path, so the preservation service is the natural home.
- **LogsController Task.Run removal** — the existing code may swallow exceptions silently inside `Task.Run`. Wrapping it in a tracker operation will surface failures through the SignalR completion event; verify no frontend code depends on the silent-failure shape.
- **Frontend cancel flow** — the 8 ad-hoc 409 shapes this plan replaces all lack `conflictingOperationId`. The frontend cancel wiring needs verification that it can parse the new shape for these three endpoints.

## Out of scope (do not do in this plan)

- Consolidating `CacheReconciliationService` eviction path onto `TrackedRemovalOperationRunner` — the background-service trigger vs user-trigger split is intentional.
- Routing `CacheManagementService` / `CacheClearingService` / `DatabaseService` whole-table clears through `GameCacheDetectionDataService` — different pattern (delete, not upsert).
- Consolidating `SteamKit2Service.Mapping.cs` orphan-depot resolution with `UnknownGameResolutionService` — different concern (PICS-time mapping vs runtime detection tracking).
- Any new controllers/services beyond the six named in the Scope section.

## Verification (after implementation)

- All commands in Phase 4 pass.
- Swarm re-audit (same W10 + W11 prompts from session `20260421-152100-1470826899`) returns zero adoption gaps in the six named files.
- Git diff shows no new `ErrorResponse`-shape 409s, no new `GetActiveOperations(...).Any()` race checks, no new raw `Task.Run` in controllers without tracker registration.
