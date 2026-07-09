import { ApiError } from '../services/apiError';

/**
 * Type guard to check if an error is an AbortError (a cancelled/aborted request). Cancellation is a
 * distinct terminal outcome, not a failure - callers use this to skip error surfacing.
 */
export function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

/**
 * Extract a display message from an unknown error. For a typed {@link ApiError} the already-composed
 * `.message` wins - `pickErrorMessage` built it with the one documented precedence
 * (`message + details + suggestion` -> `message` -> `error` -> `HTTP {status}`), so returning it
 * preserves the richer details/suggestion text; the raw body fields are only a fallback for the rare
 * empty-message case. Otherwise falls back to the Error message, then String coercion.
 */
export function getErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    return error.message || error.body?.message || error.body?.error || `HTTP ${error.status}`;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}
