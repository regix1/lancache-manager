/**
 * Module-level bridge between UniversalNotificationBar's bulk_removal cancel
 * button and the useCancellableQueue run that owns the bulk notification.
 *
 * The queue loop survives component unmount (an in-app tab switch must not
 * cancel a bulk removal), but React effects in an unmounted component no
 * longer run - so a notification-details flag alone cannot reach the loop
 * after the owning component unmounts. This registry lets the notification
 * bar cancel the live run directly, regardless of mount state.
 */
type BulkQueueCancelFn = () => void;

const activeQueueCancels = new Map<string, BulkQueueCancelFn>();

export function registerBulkQueueCancel(notificationId: string, cancelFn: BulkQueueCancelFn): void {
  activeQueueCancels.set(notificationId, cancelFn);
}

export function unregisterBulkQueueCancel(notificationId: string): void {
  activeQueueCancels.delete(notificationId);
}

export function requestBulkQueueCancel(notificationId: string): boolean {
  const cancelFn = activeQueueCancels.get(notificationId);
  if (!cancelFn) return false;
  cancelFn();
  return true;
}
