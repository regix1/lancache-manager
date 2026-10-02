import { useNotifications } from '@contexts/notifications/useNotifications';
import { useReconnectRefetch } from './useReconnectRefetch';
import type { NotificationType } from '@contexts/notifications/types';

/**
 * Calls `onEnd` once each time the repairs of `types` finish, so a page whose data a repair changed
 * reloads after it, including a page opened while the repair ran. Quiet at mount when nothing repairs.
 * It reads every repairing run, not the drawn cards: a run folded under a bulk card or one whose
 * notifications are Hidden draws no repairing card, and its repair changes the page all the same.
 */
export function useRepairEnd(types: NotificationType[], onEnd: () => void): void {
  const { repairingRuns } = useNotifications();
  const repairing = repairingRuns.some((n) => types.includes(n.type));
  useReconnectRefetch(!repairing, onEnd);
}
