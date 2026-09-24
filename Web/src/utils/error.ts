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
 * Extract a display message from an unknown error. For a typed {@link ApiError} whose body names a
 * reason, the already-composed `.message` wins - `pickErrorMessage` built it with the one documented
 * precedence (`message + details + suggestion` -> `message` -> `error`), so returning it preserves
 * the richer details/suggestion text. Unknown failures use the Error message or String coercion,
 * with empty text classified here as an unknown error.
 *
 * When the body names the refusal with a `stageKey`, that key is what the reader sees, in their own
 * language. The English sentence is the `defaultValue`, so a key this build's locale has no words
 * for still reads as a sentence rather than as a key path.
 *
 * A failure that names no reason (an empty proxy 502, an HTML gateway page, a bare 401, a fetch
 * that never reached the server, a timeout) maps to one translated sentence per class, so boxes,
 * banners and popups all say the same thing for it instead of "HTTP 502: Bad Gateway" or
 * "Failed to fetch".
 */
export function getErrorMessage(error: unknown): string {
  if (error instanceof ApiError) {
    const body = error.body;
    if (body?.stageKey) {
      return i18n.t(body.stageKey, { ...body.context, defaultValue: error.message });
    }
    // A body that names a reason is the server's own sentence, already composed by pickErrorMessage.
    if (
      body &&
      ((typeof body.message === 'string' && body.message.trim()) ||
        (typeof body.error === 'string' && body.error.trim()))
    ) {
      return error.message;
    }
    // No reason in the body: an empty proxy 502, an HTML gateway page, a bare 401 challenge or a
    // framework ProblemDetails. The status is all that is known, so it maps to one sentence per class.
    if (error.status === 502 || error.status === 503 || error.status === 504) {
      return i18n.t('common.errors.serverUnreachable');
    }
    if (error.status === 401 || error.status === 403) {
      return i18n.t('common.errors.notAuthorized');
    }
    return i18n.t('common.errors.requestFailed');
  }
  if (error instanceof Error && error.name === 'TimeoutError') {
    return i18n.t('errors.http.timeout');
  }
  // fetch() rejects with a TypeError when the server cannot be reached, worded differently by each
  // browser.
  if (
    error instanceof TypeError &&
    /failed to fetch|load failed|network ?error/i.test(error.message)
  ) {
    return i18n.t('common.errors.serverUnreachable');
  }
  const message = error instanceof Error ? error.message : String(error);
  if (!message.trim()) {
    return i18n.t('common.unknownError');
  }
  return message;
}
