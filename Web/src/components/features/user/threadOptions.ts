import type { TFunction } from 'i18next';
import type { DropdownOption } from '@components/ui/EnhancedDropdown';

const THREAD_VALUES = [1, 2, 4, 8, 16, 32, 64, 128, 256] as const;

export const getThreadOptions = (t: TFunction): DropdownOption[] => [
  { value: '', label: t('user.guest.prefill.maxThreads.noLimit') },
  ...THREAD_VALUES.map((count: number) => ({
    value: String(count),
    label: t('user.guest.prefill.maxThreads.threadsCount', { count })
  }))
];
