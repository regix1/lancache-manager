import i18n from '@/i18n';
import { ApiError } from '../services/apiError';

/**
 * Type guard to check if an error is an AbortError (a cancelled/aborted request). Cancellation is a
 * distinct terminal outcome, not a failure - callers use this to skip error surfacing.
 */
export function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

/**
 * True when the response was a 400, which is all a status code can say. It is NOT evidence that
 * the server declined rather than failed: most gated routes answer 400 for a refusal AND for a
 * fleet whose datasources disagree about their cache-key scheme, and the two are identical on the
 * wire. Softening every 400 into a notice on such a route hides the configuration failure behind
 * the refusal's wording.
 *
 * So this may only be used where the route's ONLY 400 is the decline, established by reading that
 * controller. Its one caller is the cache-size read, whose sole 400 is the download denial
 * (`CacheController.cs:134-138`); its authorization failures are 401 and 403.
 * Use {@link getErrorMessage} to read the sentence itself.
 */
export function isRefusal(error: unknown): error is ApiError {
  return error instanceof ApiError && error.status === 400;
}

/**
 * Extract a display message from an unknown error. For a typed {@link ApiError} the already-composed
 * `.message` wins - `pickErrorMessage` built it with the one documented precedence
 * (`message + details + suggestion` -> `message` -> `error` -> `HTTP {status}`), so returning it
 * preserves the richer details/suggestion text; the raw body fields are only a fallback for the rare
 * empty-message case. Otherwise falls back to the Error message, then String coercion.
 *
 * When the body names the refusal with a `stageKey`, that key is what the reader sees, in their own
 * language. The English sentence is the `defaultValue`, so a key this build's locale has no words
 * for still reads as a sentence rather than as a key path.
 */
export function getErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const message =
      error.message || error.body?.message || error.body?.error || `HTTP ${error.status}`;
    const stageKey = error.body?.stageKey;
    return stageKey ? i18n.t(stageKey, { ...error.body.context, defaultValue: message }) : message;
  }
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}
