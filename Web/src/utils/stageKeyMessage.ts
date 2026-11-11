import i18n from '@/i18n';

const UNRESOLVED_INTERPOLATION = /{{|}}/;
type StageInterpolation = Record<string, string | number | boolean | null>;

/** True when an i18n result still exposes an interpolation token to the UI. */
export function hasUnresolvedInterpolation(value: string): boolean {
  return UNRESOLVED_INTERPOLATION.test(value);
}

/**
 * A value may itself hold `{{` or `}}`, so a finished sentence cannot say whether a token went
 * unfilled. Translating with every string value blanked leaves a token only where the context
 * supplied no value for it. Numbers keep their value because they pick the plural form.
 */
function blankStringValues(context: StageInterpolation | undefined): StageInterpolation {
  return Object.fromEntries(
    Object.entries(context ?? {}).map(
      ([name, value]): [string, string | number | boolean | null] => [
        name,
        typeof value === 'string' ? '' : value
      ]
    )
  );
}

/**
 * The values the text of a log processing datasource failure names. Without a datasource there is
 * nothing to name, so the caller's translation falls back instead of printing a bare `{{token}}`.
 */
export function datasourceFailureValues(
  datasource: string | null | undefined,
  databaseError: string | null | undefined
): StageInterpolation | undefined {
  if (!datasource) return undefined;
  return databaseError ? { datasource, databaseError } : { datasource };
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
  if (!hasUnresolvedInterpolation(i18n.t(stageKey, blankStringValues(context)))) return translated;

  if (import.meta.env.DEV) {
    console.warn(`[notifications] Incomplete recovery context for stage "${stageKey}"`);
  }
  return fallback;
}

/**
 * The namespaces the API sends keys from. `signalr.` names a progress stage and `errors.` a refusal;
 * a sign-in ends on a `prefill.` key, a refusal can carry a `management.` key, and a stopped sign-in
 * a `common.` one, and each of them can become a run's message or error.
 */
const API_KEY_PREFIXES = ['signalr.', 'errors.', 'prefill.', 'management.', 'common.'];

/**
 * Translate a backend stage key, or pass through plain-text status messages.
 *
 * Every key the API sends reaches this function because an operation's status message carries
 * whichever one the producer had. No current producer composes English into a run message. Anything
 * else is a run row or schedule history entry an older version stored with its English sentence in
 * it; there is no key to look up for those, so they pass through as they were stored.
 */
export function translateStageKeyMessage(
  stageKeyOrMessage: string | undefined | null,
  context?: StageInterpolation,
  fallbackKey?: string
): string {
  if (stageKeyOrMessage) {
    const message = stageKeyOrMessage;
    return API_KEY_PREFIXES.some((prefix: string): boolean => message.startsWith(prefix))
      ? i18n.t(message, context ?? {})
      : message;
  }

  return fallbackKey ? i18n.t(fallbackKey, context ?? {}) : '';
}
