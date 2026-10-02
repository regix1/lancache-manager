import { useNotifications } from '@contexts/notifications/useNotifications';
import { useReconnectRefetch } from './useReconnectRefetch';
import type { NotificationType } from '@contexts/notifications/types';

/**
 * Calls `onEnd` once each time the repairs of `types` finish, so a page whose data a repair changed
 * reloads after it, including a page opened while the repair ran. Quiet at mount when nothing repairs.
 * It reads the drawn cards, because only a drawn card carries the `repairing` status.
 */
export function useRepairEnd(types: NotificationType[], onEnd: () => void): void {
  const { notifications } = useNotifications();
  const repairing = notifications.some((n) => types.includes(n.type) && n.status === 'repairing');
  useReconnectRefetch(!repairing, onEnd);
}
