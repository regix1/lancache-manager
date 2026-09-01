import i18n from '@/i18n';

const UNRESOLVED_INTERPOLATION = /{{|}}/;
type StageInterpolation = Record<string, string | number | boolean | null>;

/** True when an i18n result still exposes an interpolation token to the UI. */
export function hasUnresolvedInterpolation(value: string): boolean {
  return UNRESOLVED_INTERPOLATION.test(value);
}

/**
 * Translate a recovery stage without ever exposing an unresolved `{{token}}`.
 * Recovery payloads can come from an older backend with incomplete context, so
 * the caller supplies a placeholder-free fallback for that compatibility edge.
 */
export function translateRecoveryStage(
  stageKey: string | undefined | null,
  context: StageInterpolation | undefined,
  fallbackKey: string
): string {
  const fallback = i18n.t(fallbackKey);
  if (!stageKey || !i18n.exists(stageKey)) return fallback;

  const translated = i18n.t(stageKey, context ?? {});
  if (!hasUnresolvedInterpolation(translated)) return translated;

  if (import.meta.env.DEV) {
    console.warn(`[notifications] Incomplete recovery context for stage "${stageKey}"`);
  }
  return fallback;
}

/**
 * Translate a backend stage key, or pass through plain-text status messages.
 *
 * `signalr.` names a progress stage and `errors.` names a refusal; both are keys the API sends for
 * the browser to render, and both reach this function because an operation's status message carries
 * whichever one the producer had. Anything else is a sentence the backend composed at runtime, such
 * as a count or a path it filled in itself. There is no key to look up for those, so passing them
 * through is the only thing left to do, and it is what the reader gets today.
 */
export function translateStageKeyMessage(
  stageKeyOrMessage: string | undefined | null,
  context?: StageInterpolation,
  fallbackKey?: string
): string {
  if (stageKeyOrMessage?.startsWith('signalr.') || stageKeyOrMessage?.startsWith('errors.')) {
    return i18n.t(stageKeyOrMessage, context ?? {});
  }

  if (stageKeyOrMessage) {
    return stageKeyOrMessage;
  }

  return fallbackKey ? i18n.t(fallbackKey, context ?? {}) : '';
}
