import type { TabId } from './types';

// TabId uses 'hit-ratio' but the i18n keys use 'hitRatio', so the two vocabularies
// need an explicit map instead of building the key from the tab value.
export const TAB_DESCRIPTION_KEYS: Record<TabId, string> = {
  service: 'service',
  'hit-ratio': 'hitRatio',
  bandwidth: 'bandwidth',
  misses: 'misses',
  games: 'games'
};
