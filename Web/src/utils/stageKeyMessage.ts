import i18n from '@/i18n';

const UNRESOLVED_INTERPOLATION = /{{|}}/;

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
  context: Record<string, string | number | boolean> | undefined,
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

/** Translate a backend stage key, or pass through plain-text status messages. */
export function translateStageKeyMessage(
  stageKeyOrMessage: string | undefined | null,
  context?: Record<string, string | number | boolean>,
  fallbackKey?: string
): string {
  if (stageKeyOrMessage?.startsWith('signalr.')) {
    return i18n.t(stageKeyOrMessage, context ?? {});
  }

  if (stageKeyOrMessage) {
    return stageKeyOrMessage;
  }

  return fallbackKey ? i18n.t(fallbackKey, context ?? {}) : '';
}
