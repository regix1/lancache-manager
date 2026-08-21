import type { TFunction } from 'i18next';
import i18n from '@/i18n';
import { parseUtcDate } from './timezone';

export function formatMinutes(minutes: number, t: TFunction): string {
  const minuteUnit = t('widgets.eventCompare.units.minutes');
  const hourUnit = t('widgets.eventCompare.units.hours');
  if (minutes < 60) {
    return `${minutes}${minuteUnit}`;
  }

  const hours = Math.floor(minutes / 60);
  const remainder = minutes % 60;
  if (hours < 24) {
    return remainder > 0 ? `${hours}${hourUnit} ${remainder}${minuteUnit}` : `${hours}${hourUnit}`;
  }

  const days = Math.floor(hours / 24);
  const leftoverHours = hours % 24;
  const dayUnit = t('widgets.eventCompare.units.days');
  return leftoverHours > 0 ? `${days}${dayUnit} ${leftoverHours}${hourUnit}` : `${days}${dayUnit}`;
}

export function formatSessionTimeRemaining(expiresAt: string | null): string | null {
  if (!expiresAt) return null;
  const diff = parseUtcDate(expiresAt).getTime() - Date.now();
  if (diff <= 0) return null;
  return formatMinutes(Math.floor(diff / (1000 * 60)), i18n.t.bind(i18n));
}
