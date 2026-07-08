import type { useTranslation } from 'react-i18next';

/** react-i18next's translate function, as returned by `useTranslation()`. */
type TranslateFn = ReturnType<typeof useTranslation>['t'];

/**
 * Relative "last run" label shared by the generic schedule cards and the Scheduled Prefill
 * per-service rows: Never / Just now / {count}m|h|d ago. Kept framework-agnostic (it takes the
 * `t` function) so it can live outside a component file and honour the Fast-Refresh rule that
 * `.tsx` files export only React components.
 */
export function formatLastRun(lastRunUtc: string | null, t: TranslateFn): string {
  if (!lastRunUtc) {
    return t('management.schedules.neverRun');
  }
  const date = new Date(lastRunUtc);
  const diffMs = Date.now() - date.getTime();
  const diffSeconds = Math.floor(diffMs / 1000);
  if (diffSeconds < 60) {
    return t('management.schedules.justNow');
  }
  const diffMinutes = Math.floor(diffSeconds / 60);
  if (diffMinutes < 60) {
    return t('management.schedules.minutesAgo', { count: diffMinutes });
  }
  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) {
    return t('management.schedules.hoursAgo', { count: diffHours });
  }
  const diffDays = Math.floor(diffHours / 24);
  return t('management.schedules.daysAgo', { count: diffDays });
}
