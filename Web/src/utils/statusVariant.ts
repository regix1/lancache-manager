import type { BadgeVariant } from '@components/ui/Badge.types';

/**
 * Run lifecycle status -> the one Badge variant that status is drawn in, everywhere.
 *
 * Keys are lowercase because callers normalise first: the same state arrives as
 * `InProgress` from the prefill history and `running` from the operation stream.
 *
 * A status with no row here is outside the run vocabulary, so each caller supplies its
 * own fallback rather than the map guessing on its behalf.
 *
 * A run the user stopped is not a fault, so `cancelled` is grey and red keeps meaning
 * something broke.
 *
 * A skipped run did nothing because there was nothing to do or it was put off, so it is grey too;
 * a run that could not start because something is missing ends failed on the server and is red.
 *
 * Event lifecycle (`active` / `upcoming` / `past`) is a separate vocabulary and is
 * deliberately not folded in here.
 */
export const VARIANT_BY_STATUS: Record<string, BadgeVariant> = {
  running: 'info',
  inprogress: 'info',
  completed: 'success',
  failed: 'error',
  error: 'error',
  cancelled: 'neutral',
  waiting: 'waiting',
  repairing: 'repairing',
  skipped: 'neutral'
};
