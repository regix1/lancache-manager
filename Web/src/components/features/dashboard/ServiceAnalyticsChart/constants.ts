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

// Sentinel for the Games view's service filter. Deliberately not the empty string: the dropdown
// treats an empty value as "nothing selected" and would fall back to its placeholder. Real service
// keys come from the detection payload (steam, epic, xbox, blizzard, riot, wsus), so this cannot
// collide with one.
export const ALL_GAME_SERVICES = 'all-services';

// A detected game with no service recorded is a Steam depot: the other services all set the
// field explicitly. The chart's slice builder already assumes this, so the filter must agree
// with it or a Steam game would be reachable from the chart but not from the picker.
export const DEFAULT_GAME_SERVICE = 'steam';
