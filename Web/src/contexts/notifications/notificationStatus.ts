/**
 * Status predicates shared by the notification context and the notification bar.
 *
 * A cancelled operation is terminal exactly like completed/failed: nothing further
 * arrives for it, so its card is dismissible by hand. The same holds for a skipped
 * run, which stopped because it had nothing to do.
 * Keeping this in one place stops the call sites from drifting apart again.
 */

import type { NotificationStatus, NotificationTerminal, UnifiedNotification } from './types';

const TERMINAL_STATUSES: readonly NotificationStatus[] = [
  'completed',
  'failed',
  'cancelled',
  'skipped'
];

/** True when the operation has reached a final state and will emit nothing further. */
export function isTerminalNotificationStatus(
  status: NotificationStatus
): status is Extract<NotificationStatus, NotificationTerminal['status']> {
  return TERMINAL_STATUSES.includes(status);
}

// A repairing card's job already ended, so no cancel can reach it, as with a terminal card.
export const cancelUnreachable = (notification: UnifiedNotification): boolean =>
  isTerminalNotificationStatus(notification.status) || notification.status === 'repairing';
