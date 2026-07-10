import type { ClientStat } from '@/types';

/** Sort field for the Clients list. */
export type SortOption =
  | 'ip'
  | 'downloads'
  | 'totalData'
  | 'hits'
  | 'misses'
  | 'hitRate'
  | 'lastActivity';
export type SortDirection = 'asc' | 'desc';

/**
 * Re-exported instead of a duplicated local interface: ClientStat already
 * describes exactly what a client row needs, and a separate ClientData
 * shape (with a weaker optional isGrouped) would only drift from it.
 */
export type { ClientStat };
