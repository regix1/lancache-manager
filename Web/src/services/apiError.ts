/**
 * The frontend error taxonomy and single typed API error.
 *
 * This module is intentionally dependency-free (it imports nothing from the app) so that both the
 * heavy `api.service.ts` and the small sibling services (`auth`, `operationState`, `preferences`,
 * `theme`) can pull it in without a circular dependency and without dragging the whole API surface
 * into their bundle. See the error-handling standard (§4.1).
 */

/**
 * Closed taxonomy classifying an API failure by the boundary that produced it. Callers branch on
 * this structurally (`error instanceof ApiError && error.kind === 'conflict'`) instead of sniffing
 * the message string. Not exported: comparisons against the string literals need no named type.
 */
type ApiErrorKind = 'auth' | 'forbidden' | 'conflict' | 'http' | 'parse' | 'network';

/**
 * The backend error body shape parsed by the API layer. Mirrors the C# `ErrorResponse`
 * (`{ error, details }`) plus the richer structured error (`{ message, details, suggestion }`) and
 * the legacy `{ error }` field. Every field is optional because different endpoints and the global
 * exception middleware each emit a different subset.
 */
export interface ApiErrorData {
  code?: string;
  message?: string;
  error?: string;
  details?: string;
  suggestion?: string;
}

/**
 * Structured payload the backend returns for an HTTP 409 Conflict (matches C#
 * `OperationConflictResponse`, camelCase). A conflict is NOT a hard failure - the queue returns
 * 202/409 by design - so its full body is preserved on `ApiError.cause` for i18n stageKey lookup.
 */
interface OperationConflictBody {
  code: string;
  stageKey: string;
  error: string;
  activeOperationId?: string | null;
  activeOperationType?: string | null;
  activeOperationScope?: string | null;
  context?: Record<string, unknown> | null;
}

interface ApiErrorInit {
  message: string;
  status: number;
  kind: ApiErrorKind;
  body: ApiErrorData | null;
  cause?: unknown;
}

/**
 * The ONE typed error thrown by the API layer (`ApiService.handleResponse`, `assertOk`,
 * `buildApiError`). Extends `Error` so the 123 existing `catch (error: unknown)` / `throw error`
 * call sites stay zero-touch, and adds `status` / `kind` / `body` / `cause` so callers can branch
 * structurally instead of parsing the message text.
 *
 * Cancellation is deliberately NOT an `ApiError`: a 499 / aborted request keeps
 * `name === 'AbortError'` so `isAbortError` catches it (cancellation is a distinct terminal
 * outcome, never a failure).
 */
export class ApiError extends Error {
  readonly status: number;
  readonly kind: ApiErrorKind;
  readonly body: ApiErrorData | null;
  readonly cause?: unknown;

  constructor(init: ApiErrorInit) {
    super(init.message);
    this.name = 'ApiError';
    this.status = init.status;
    this.kind = init.kind;
    this.body = init.body;
    this.cause = init.cause;
  }
}

/**
 * Apply the ONE message precedence for a parsed backend error body:
 * `message + details + suggestion` (full structured error) -> `message` -> `error` (legacy) ->
 * `HTTP {status}: {raw text or statusText}`. Every failure status funnels through this so the
 * 401/403/409 branches no longer each pick a bespoke subset.
 */
function pickErrorMessage(body: ApiErrorData | null, rawText: string, response: Response): string {
  if (body) {
    if (body.message && body.details && body.suggestion) {
      return `${body.message}\n\n${body.details}\n\n${body.suggestion}`;
    }
    if (body.message) {
      return body.message;
    }
    if (body.error) {
      return body.error;
    }
  }
  return `HTTP ${response.status}: ${rawText || response.statusText}`;
}

/**
 * Build the single typed `ApiError` from a failed `Response`. Reads the body exactly once, applies
 * the one message precedence, classifies `kind` by status, performs the per-status side effects
 * (dispatch the auth-refresh event on 401, warn on 403) and preserves a structured 409
 * `OperationConflictBody` as `cause`.
 *
 * Callers must have already established `!response.ok` and handled 499 as cancellation first
 * (`assertOk` / `handleResponse` do this).
 */
export async function buildApiError(response: Response): Promise<ApiError> {
  const status = response.status;

  if (status === 401) {
    // Trigger an auth refresh so the app re-evaluates the session (moved verbatim from the old
    // per-status branch in handleResponse).
    window.dispatchEvent(new Event('auth-state-changed'));
  } else if (status === 403) {
    console.warn('403 Forbidden: Access denied. User may lack required permissions.');
  }

  // ONE body read for every failure status.
  const rawText = await response.text().catch(() => '');
  let body: ApiErrorData | null = null;
  try {
    body = rawText ? (JSON.parse(rawText) as ApiErrorData) : null;
  } catch {
    // Non-JSON body (plain text / empty): leave `body` null; `rawText` is the message fallback.
  }

  // A structured 409 keeps its full body on `cause` for i18n stageKey lookup - the one place
  // structured conflict data survives on the thrown error.
  let cause: unknown;
  if (status === 409) {
    const conflict = body as (ApiErrorData & Partial<OperationConflictBody>) | null;
    if (conflict && (conflict.code === 'OPERATION_CONFLICT' || conflict.stageKey)) {
      cause = conflict as OperationConflictBody;
    }
  }

  const kind: ApiErrorKind =
    status === 401 ? 'auth' : status === 403 ? 'forbidden' : status === 409 ? 'conflict' : 'http';

  return new ApiError({
    status,
    kind,
    body,
    cause,
    message: pickErrorMessage(body, rawText, response)
  });
}

/**
 * Body-less counterpart to `ApiService.handleResponse`, for endpoints with no response body to
 * parse (void POST/PUT/PATCH/DELETE). On a failed response it throws the SAME typed error a full
 * `handleResponse` would (499 -> `AbortError`, otherwise `ApiError` via `buildApiError`); on
 * success it returns the untouched `Response`. Use it so every bypass path funnels through the one
 * throw site instead of hand-rolling `if (!res.ok) throw new Error(...)`.
 */
export async function assertOk(response: Response): Promise<Response> {
  // Cancellation stays the AbortError path, never an ApiError.
  if (response.status === 499) {
    const error = new Error('Request cancelled');
    error.name = 'AbortError';
    throw error;
  }
  if (!response.ok) {
    throw await buildApiError(response);
  }
  return response;
}
