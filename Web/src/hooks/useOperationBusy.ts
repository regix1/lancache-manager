import { useMemo } from 'react';
import { useNotifications } from '@contexts/notifications/useNotifications';
import type { NotificationType, NotificationStatus } from '@contexts/notifications/types';

/**
 * Options for {@link useOperationBusy}. Strongly typed (no loose params) so the
 * dependency array stays stable.
 *
 *  - `types`  - the notification types to scan for.
 *  - `status` - the status(es) that count as "busy". Defaults to `'running'`.
 */
interface OperationBusyOptions {
  types: NotificationType[];
  status?: NotificationStatus | NotificationStatus[];
}

/**
 * Returns `true` when at least one server run matches the given `types` and
 * one of the given `status` values (default `'running'`). The type-only sibling
 * of `useIsEntityBusy` (which additionally matches an entity identity); use this
 * when the busy check is purely "is operation X in state Y" with no identity.
 *
 * Reads the server's run list, Hidden runs included: a run the user chose not to
 * see still keeps its page's buttons busy until it ends. A scan's hidden phase is
 * not in the list (it is folded under its parent run), so an eviction scan's
 * detection phase never marks game detection busy.
 *
 * Mirrors the `useIsEntityBusy` memoization pattern: the option fields are
 * destructured to stable primitives before the `useMemo` so the array passed in
 * a render literal (`{ types: ['x'] }`) does not force a re-scan every render
 * unless its contents actually change.
 */
export function useOperationBusy(options: OperationBusyOptions): boolean {
  const { runs } = useNotifications();

  const { types, status } = options;
  // Normalize the status filter to an array of strings so the memo key is a
  // stable primitive join rather than a possibly-new array/string reference.
  const statusKey =
    status === undefined ? 'running' : Array.isArray(status) ? status.join(',') : status;
  const typesKey = types.join(',');

  // Parse the comma-joined keys back into arrays once per key change, so the
  // scan memo below re-allocates the lookup arrays only when the inputs actually
  // change rather than on every scan run.
  const statuses = useMemo(() => statusKey.split(',') as NotificationStatus[], [statusKey]);
  const typeList = useMemo(() => typesKey.split(',') as NotificationType[], [typesKey]);

  return useMemo(
    () => runs.some((n) => typeList.includes(n.type) && statuses.includes(n.status)),
    [runs, typeList, statuses]
  );
}
