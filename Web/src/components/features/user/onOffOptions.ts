import type { ComponentProps } from 'react';
import type { TFunction } from 'i18next';
import type { ToggleSwitch } from '@components/ui/ToggleSwitch';

export const getOnOffOptions = (t: TFunction): ComponentProps<typeof ToggleSwitch>['options'] => [
  { value: 'off', label: t('common.off') },
  { value: 'on', label: t('common.on'), activeColor: 'success' }
];
