# Error-Handling Standard — Lancache Manager (house rules)

Applies to: C# backend, Rust processor, React/TS frontend

This is the binding house standard for how each layer classifies, propagates, logs, and surfaces
errors — grounded in patterns that already exist in this repo, not new architecture.

Every rule is grounded in a pattern that ALREADY EXISTS in this repo; the goal is uniform
adoption, not new architecture. File references are the canonical examples to copy.

---

## 0. Why this exists

Auditing all three stacks found the same thing: the correct pattern already exists and is
used in the best files, but a long tail of sites each reinvented error handling. Result: 9 backend
error-response shapes, 3 frontend message-precedences, ~57% of frontend catches invisible to the
user, and Rust failures that reach C# as an exit code with no reason attached. This document is the
ONE way each layer handles errors from now on.

## 1. Universal principles (all layers)

1. **One funnel per surface.** Exactly one place turns raw failures into the outward contract:
   `GlobalExceptionMiddleware` (HTTP), the stdout `failed`/`complete` event (Rust), the notification
   registry (frontend UI). No scattered translate-and-return.
2. **Classify into a small, closed taxonomy at the boundary** — typed C# exceptions, a typed
   frontend `ApiError`, Rust `emit_failed` stage keys. Small and stable, not per-site bespoke.
3. **Propagate with context; never lose the real message.** `.context("…")` (Rust), message
   templates (C#), `body`/`cause` on `ApiError` (TS). The next consumer needs fields, not prose.
4. **Never swallow silently.** Banned outside tests / explicitly-documented best-effort cleanup:
   empty `catch {}`, `unwrap()/expect()` on runtime values, `console.error`-only for a user-facing
   failure, an `IHubFilter`/handler that returns without logging.
5. **Log/emit the structured object, not the stringified message.** `_logger.LogError(ex, "…{Arg}", arg)`
   never `ex.Message` alone; NDJSON on stdout not a sentence; typed `ApiError` not `catch (e: any)`.
6. **Cancellation is a first-class terminal outcome, distinct from failure.** The tri-layer status
   vocabulary `OperationStatus` (C#) ≡ `NotificationStatus` (TS) ≡ Rust `status` string stays aligned:
   `running / completed / failed / cancelled` (the literal is `"completed"`, never `"complete"`).
   Cancel is NEVER logged as an error and NEVER a 5xx (it is 499 / `emit_cancelled` / `isAbortError`).
7. **No fallback defaults for required fields.** A swallowed error must never masquerade as empty
   data (`catch { return [] }`, `{} as T`). Surface it, or make "empty" an explicit, documented result.

## 2. C# backend

### 2.1 HTTP errors — throw typed, let the middleware translate
- **The single HTTP funnel is `GlobalExceptionMiddleware`** (`Middleware/GlobalExceptionMiddleware.cs`,
  registered `Program.cs:933`). `CrudControllerBase` (zero try/catch) is the exemplar.
- **DON'T** `catch (Exception ex) { return StatusCode(500, ... ex.Message ...); }` — this leaks
  internals past the middleware's dev/prod gate (7 sites do this today) and forks the error shape.
- **DO** let unexpected exceptions propagate. When a side-effect is required first (e.g.
  `CompleteOperation(success:false)`), use the `CacheController.cs:490` pattern:
  `catch (Exception ex) { _logger.LogError(ex, "…"); /* side-effect */; throw; }`.
- **Expected 4xx**: throw a typed exception (`throw new NotFoundException("Session")`,
  `throw new ValidationException("…")`) OR, for a guard-clause body, return via the `ApiResponse.*`
  factory (`Models/ApiResponses.cs`) — `return BadRequest(ApiResponse.Error("…"))`. NEVER an
  anonymous `new { error }` / `new { message }` / plain string / bespoke `SetupErrorResponse` /
  `MessageResponse`-for-error.

### 2.2 One error body shape
- Canonical wire shape: **`{ error, details?, statusCode, traceId? }`** (camelCase, nulls omitted).
  `error` is the ONE message key repo-wide — retire `{ message }` for errors.
- Unify the `ErrorResponse` DTO (`Models/ApiResponses.cs`) with the middleware's shape so the two
  "canonical" surfaces stop disagreeing on the third field. Add `traceId = HttpContext.TraceIdentifier`.
- Expand the typed-exception set (all live in / next to `GlobalExceptionMiddleware.cs`):
  `NotFoundException`→404, `ValidationException`→400, **new** `ConflictException`→409,
  **new** `ForbiddenException`→403, **new** `ServiceUnavailableException`→503.
  Everything unmapped → 500 safe message.
- `ServiceUnavailableException` is for **a dependency being down or unreachable** (Docker not running,
  a remote download host failing) — not for anything the caller sent and not for a fault in our own
  code. Use it only when the message names the dependency and says what to do about it, because the
  middleware passes it through in production the way it does for the other four. A server-state fault
  with no such message stays an ordinary exception and keeps the generic 500.
- ⚠️**Check the route to the client before converting a throw.** `Hubs/PrefillDaemonHubBase.cs` catches
  `InvalidOperationException` **by type** and forwards its message to the SignalR caller. The typed
  exceptions derive from `Exception`, so converting a hub-reachable throw makes it fall through to the
  generic catch and the user learns LESS. Convert only throws that are HTTP-only.
- **Queue conflicts stay `202 Accepted`** via `_operationQueue.EnqueueAsync` — NEVER 409, never an
  error. **Cancellation stays 499** — standardize on the middleware's no-body 499 (remove the manual
  `CacheController.cs:485` 499 body).
- **Decision flagged for approval:** `InvalidOperationException` currently → 400. Many IOEs are
  server-state faults (should be 500). Recommend: IOE→500, and introduce/using a domain exception for
  the genuinely-client-caused 400 cases.

### 2.3 Services & background work
- Inherit the loop-guard base classes (`Infrastructure/Services/Base/ScheduledBackgroundService.cs`,
  `ConfigurableScheduledService.cs`) — the GOLD pattern (cancel = clean break; failure = LogError with
  the exception object, then continue). **Reconcile the two**: both guard OCE on `stoppingToken`
  (so an inner-timeout OCE can't silently kill the loop) and both back off `ErrorRetryDelay` after a
  loop error.
- **Rust failures → a typed `RustProcessException`** thrown by `RustProcessHelper.EnsureSuccess(tool, ctx)`
  (no-op when `ExitCode == 0`). Replaces the 8 generic `throw new Exception($"… exit code …")` sites;
  the exception carries `ExitCode` + stderr so callers can `catch (RustProcessException)`.
- **Logging:** always `_logger.Log{Level}(ex, "template {Named}", args)` — pass the exception object
  (fixes the 2 `.Message`-only sites). `LogError` = genuine/unexpected; `LogWarning` = expected/tolerated
  (validation, unique-constraint, best-effort); `LogInformation` = cancellation.
- **Cancellation:** its own path — `LogInformation` + `_operationTracker.CompleteOperation(id,
  success:false, error:"Cancelled by user")` (the one standardized shape), never `LogError`.
- **No silent defaults on required fields.** Read-path soft-null (`return null/[]/0` after
  `LogWarning(ex, …)`) is allowed ONLY with an XML-doc note that null == "failed or empty".

### 2.4 Long-running operation terminals (SignalR)
- Every long-running op MUST terminate via its `*Complete` event. Failure =
  `Success=false, Status=OperationStatus.Failed, Error=<message>, Cancelled=false`; cancel =
  `Cancelled=true`.
- Introduce **one shared terminal base composed from the existing
  `OperationTerminalInfo(Success, Cancelled, Error)`** (`Models/OperationTerminalInfo.cs`, currently
  declared-but-unused). Refit the 16 `*Complete` records (`Infrastructure/Utilities/SignalRNotifications.cs`
  + `Models/CacheSizeScanModels.cs` + `Models/EvictionScanModels.cs`) to compose it — this guarantees
  `Error` on the 4 that lack it (`GameRemovalComplete`, `ServiceRemovalComplete`, `GameDetectionComplete`,
  `CacheScanComplete`).
- **Add `ISignalRNotificationService.NotifyOperationFailedAsync(op, error)`** so a Rust `Success=false`
  (or any service failure) ALWAYS reaches the notification registry uniformly, instead of each caller
  hand-rolling (or forgetting) the failure broadcast.
- **SignalR hubs:** keep throwing `HubException(message)` for synchronous client-visible errors. Add
  ONE `IHubFilter` (registered in `AddSignalR`) that converts any uncaught hub exception into a logged
  generic `HubException` — the hub-side parallel to the HTTP global handler. Set `EnableDetailedErrors=false`
  explicitly + documented.
- Out of scope for this pass (optional later): Serilog / structured JSON logs / a request `traceId`
  correlation column. `traceId` in the HTTP body (2.2) is included; wiring it into every log line is not.

## 3. Rust (`rust-processor` crate)

- **Keep `anyhow`. Do NOT introduce `thiserror`** — these are leaf CLI binaries; typed enums buy
  nothing and would be churn. `anyhow::Result` + `?` + `.context("…")` is already the majority pattern.
- **Every binary is `fn main() -> anyhow::Result<()>`** (async: `#[tokio::main] async fn main() -> Result<()>`).
  Convert the 5 bare-`main` bins (`cache_clear`, `cache_size`, `log_service_manager`, `db_reset`, and the
  stray `process::exit(0)` in `log_processor`).
- **All fatal failures route through the stdout event channel.** Extend `progress_events.rs` with an
  always-present **`errorDetail`** field (the full `anyhow` chain, `format!("{e:#}")`) on the failed
  event, and add a top-level **`run()` catch-and-emit helper**: on `Ok` → `emit_complete` + exit 0;
  on `Err(e)` → `emit_failed(stageKey, { errorDetail })` **and** `eprintln!("{e:#}")` **and** exit 1.
  Wire every binary's body through it so `?`-propagated errors also produce a structured terminal event
  (today they reach C# as stderr + exit-1 only).
- Keep stderr for human logs; keep the `--progress` gate for the envelope, but ALWAYS `eprintln!` the
  chain + exit 1 so no-progress callers still get a reason.
- **Ban `std::process::exit(1)` for failure** — return `Err(..)` so exit code + terminal event are
  produced in one place. `process::exit(0)` early-success discouraged; prefer `Ok(())`.
- **No `unwrap()/expect()` on runtime values** outside `#[cfg(test)]`. Fix the ~4 genuinely risky
  production sites (`log_processor.rs:1230`, `cache_eviction_scan.rs:479` & `:485`, `cache_size.rs:1521`
  — the serialize-unwrap inside the error path) → `?` + `.context(...)` / `.ok_or_else(|| anyhow!(...))`.
  Constant-regex / compile-embedded-asset unwraps may stay. (Optional CI enforcement: `clippy::unwrap_used`
  / `clippy::expect_used` = deny on non-test code.)
- **Exit codes:** `0` success (cancel = clean stop = 0), `1` any failure. No richer codes — the host
  reads the terminal event's `status`/`success`/`errorDetail`, not the numeric code.
- Host side: `RustProcessHelper.cs` / progress monitor read the new `errorDetail` field into the C#
  failure path (feeds `NotifyOperationFailedAsync`).

## 4. Frontend (React + TypeScript)

### 4.1 API layer — one typed error, one throw site
- Introduce **one `ApiError extends Error`** in a non-`.tsx` module (`services/apiError.ts` or
  `utils/error.ts`, per the Fast-Refresh export rule):
  `class ApiError extends Error { readonly status: number; readonly kind: 'auth'|'forbidden'|'conflict'|'http'|'parse'|'network'; readonly body: ApiErrorData | null; readonly cause?: unknown }`.
- **`ApiService.handleResponse<T>` (`api.service.ts:248`, 123 call sites) is the single throw site**:
  one body-parse, one message precedence (`message+details+suggestion → message → error → HTTP {status}`),
  then `throw new ApiError({ status, kind, body, message, cause })`. The 401/403/409 branches stop having
  bespoke precedences — they only set `kind` (+ dispatch the auth event / attach the conflict body as
  `cause`). Because `ApiError extends Error`, the 123 callers and their `catch (error: unknown)` /
  `throw error` are **zero-touch**.
- **Callers consume structurally**, never by message-sniffing:
  `if (error instanceof ApiError && error.status === 409) …`, `error.body?.suggestion`.
  `getErrorMessage` (`utils/error.ts:11`) gains an `ApiError` branch.
- **Route the bypass paths through it**: the ~14 raw-fetch methods + 4 hand-rolled sibling services
  (`auth`, `operationState`, `preferences`, `theme`) call `handleResponse` (or a thin `assertOk(res)`
  that throws the same `ApiError`). `auth.service`'s deliberate `{success,message}` return contract may
  stay but must parse via the shared shape (document the exception).
- **Cancellation stays the `AbortError` path** — 499/abort still set `name==='AbortError'`; `isAbortError`
  remains the guard. Do NOT fold cancel into `ApiError`.
- **No swallow-return-default** (`getAvailableGameImages` `catch { return [] }`, `getImageCacheVersion`
  `return 0`, `theme.service:545 return null`, `{} as T`) — surface the error or make "empty" explicit.
- `catch (error: unknown)` everywhere (fix the ~20 bare `catch (error)`); keep the zero-`any` status.

### 4.2 Surfacing — one hook, one registry, three routes
- Add **one `useErrorHandler` hook** (`hooks/useErrorHandler.ts`) that COMPOSES existing pieces
  (`getErrorMessage` + `useNotifications().addNotification`) — it adds NO new channel. Export a
  strongly-typed `notifyError(userMessage: string, error?: unknown, opts?: { silent?: boolean; logLabel?: string })`.
- **3-way routing rule:**
  - **Transient / one-shot action failure** (button: auth, save, revoke, import) → notification
    registry via `notifyError`. Replaces the 34 prop-drilled `showToast` sites + the user-facing
    console-only sites.
  - **Persistent / form-scoped validation, or a section that failed to load** → inline
    `<Alert color="red">` from local error state (keep the 42 pattern-C sites; normalize on `color="red"`
    + `getErrorMessage`).
  - **Render-phase crash** → `ErrorBoundary`. Add **section-level boundaries around each Management tab**
    so one crash doesn't blank the app; give `componentDidCatch` the `notifyError` sink.
  - **Cancellation** (`AbortError`) → silent (mirror `isAbortError` in the hook).
- **Never show raw `err.message`.** Always `getErrorMessage(err)` for the technical detail (into
  `details`/console) and an i18n key for the displayed string: `notifyError(t('feature.errors.doThing'), err)`.
  Route the ~85 raw `.message` refs through `getErrorMessage`.
- **Providers/hooks (the 82 console-only sites):** user-facing failures → `notifyError`; genuine
  background noise (poll retries) → explicit `notifyError(..., { silent: true })` — silence becomes a
  reviewable decision, not an accidental `console.error`.

## 5. Enforcement / rollout
- This doc lives at `docs/error-handling-standard.md` and the repo's root instruction file points at
  it, so anyone touching error handling reads it first.
- Optional lint gates (proposed, not blocking): Rust `clippy::unwrap_used`/`expect_used` deny on
  non-test code; an ESLint guard against `catch (e: any)` (already clean — a ratchet); a controller
  analyzer/test forbidding anonymous error-object returns.

## 6. Resolved design decisions (user-approved 2026-07-09)
1. **Backend HTTP handler — KEEP + extend the custom `GlobalExceptionMiddleware`.** Do NOT migrate to
   `IExceptionHandler`/`AddProblemDetails`. Unify the body shape, add `ConflictException`/`ForbiddenException`,
   add `traceId`.
2. **`InvalidOperationException` → RECLASSIFY to 500.** Introduce/use a domain exception (e.g.
   `ValidationException` or a new `ConflictException`/`ForbiddenException`) for the genuinely client-caused
   cases that should stay 4xx. This is a deliberate HTTP-contract change.
3. **499 cancellation → NO BODY.** Remove the manual `CacheController.cs:485` 499 body so every
   cancellation matches the middleware's no-body 499.
4. **Scope — ALL THREE LAYERS** (C# backend, Rust, frontend), phased per `plan.md`.
5. **traceId — HTTP error body only.** Full Serilog / per-log correlation is OUT of scope this pass.
