import type { TFunction } from 'i18next';
import { parseUtcDate } from '@utils/timezone';

// The Users page writes its times as "12 min ago" and "4 h 59 min left"; the app's shared relative
// formatter writes "12m ago", so the page counts its own units. A session that was never seen has
// no last-seen time.
export const formatLastSeen = (dateString: string | null, t: TFunction): string => {
  if (!dateString) return t('activeSessions.relative.never');

  const minutes = Math.floor((Date.now() - parseUtcDate(dateString).getTime()) / (1000 * 60));
  const hours = Math.floor(minutes / 60);
  const days = Math.floor(hours / 24);

  if (days > 0) return t('user.panes.time.daysAgo', { count: days });
  if (hours > 0) return t('user.panes.time.hoursAgo', { count: hours });
  if (minutes > 0) return t('user.panes.time.minutesAgo', { count: minutes });
  return t('user.panes.time.justNow');
};

export const formatTimeLeft = (expiresAt: string, t: TFunction): string => {
  const now = new Date();
  const expiry = parseUtcDate(expiresAt);
  const diff = expiry.getTime() - now.getTime();

  if (diff <= 0) return t('activeSessions.prefill.status.expired');

  const hours = Math.floor(diff / (1000 * 60 * 60));
  const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));

  if (hours > 0) {
    return t('user.panes.time.hoursMinutesLeft', {
      hours,
      minutes
    });
  }
  return t('user.panes.time.minutesLeft', {
    minutes
  });
};
