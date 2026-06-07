import i18n from '@/i18n';

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
