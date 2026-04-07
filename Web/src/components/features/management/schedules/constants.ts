import type { DropdownOption } from '@components/ui/EnhancedDropdown';
import type { TFunction } from 'i18next';

export const getScheduleIntervalOptions = (t: TFunction): DropdownOption[] => [
  { value: '0', label: t('management.schedules.intervals.disabled') },
  { value: '-1', label: t('management.schedules.intervals.startupOnly') },
  { value: '0.5', label: t('management.schedules.intervals.every30Minutes') },
  { value: '1', label: t('management.schedules.intervals.everyHour') },
  { value: '3', label: t('management.schedules.intervals.every3Hours') },
  { value: '6', label: t('management.schedules.intervals.every6Hours') },
  { value: '12', label: t('management.schedules.intervals.every12Hours') },
  { value: '24', label: t('management.schedules.intervals.daily') },
  { value: '48', label: t('management.schedules.intervals.every2Days') },
  { value: '168', label: t('management.schedules.intervals.weekly') },
  { value: '336', label: t('management.schedules.intervals.every2Weeks') },
  { value: '720', label: t('management.schedules.intervals.monthly') }
];
