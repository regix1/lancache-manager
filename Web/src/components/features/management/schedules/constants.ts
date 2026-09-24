import type { DropdownOption } from '@components/ui/EnhancedDropdown';
import type { TFunction } from 'i18next';
import type { NotificationDisplayMode } from './types';

export const getScheduleIntervalOptions = (t: TFunction): DropdownOption[] => [
  { value: '0', label: t('management.schedules.intervals.disabled') },
  { value: '-1', label: t('management.schedules.intervals.startupOnly') },
  { value: '1', label: t('management.schedules.intervals.everyHour') },
  { value: '3', label: t('management.schedules.intervals.every3Hours') },
  { value: '6', label: t('management.schedules.intervals.every6Hours') },
  { value: '12', label: t('management.schedules.intervals.every12Hours') },
  { value: '24', label: t('management.schedules.intervals.daily') },
  { value: '48', label: t('management.schedules.intervals.every2Days') },
  { value: '168', label: t('management.schedules.intervals.weekly') },
  { value: '336', label: t('management.schedules.intervals.every2Weeks') },
  { value: '720', label: t('management.schedules.intervals.monthly') },
  { value: 'custom', label: t('management.schedules.intervals.custom') }
];

// The page header chooses the global style; its rows can follow that choice or override it.
export const getNotificationStyleOptions = (
  t: TFunction,
  defaultMode?: NotificationDisplayMode
): DropdownOption[] => [
  ...(defaultMode
    ? [
        {
          value: 'default',
          label: t('management.schedules.notificationStyleDefault', {
            style: t(`management.schedules.notificationStyle.${defaultMode}`)
          }),
          description: t('management.schedules.notificationStyleDefaultDescription')
        }
      ]
    : []),
  {
    value: 'full',
    label: t('management.schedules.notificationStyle.full'),
    description: t('management.schedules.notificationStyle.fullDescription')
  },
  {
    value: 'condensed',
    label: t('management.schedules.notificationStyle.condensed'),
    description: t('management.schedules.notificationStyle.condensedDescription')
  }
];
