/**
 * Canonical operation lifecycle status used across SignalR progress events and
 * the notification system. Mirrors the backend `OperationStatus` enum.
 */
export type OperationStatus =
  | 'pending'
  | 'running'
  | 'cancelling'
  | 'completed'
  | 'failed'
  | 'cancelled';

/**
 * Variant used by transient system toast/banner events (ShowToast).
 * Keep in sync with backend `NotificationVariant` / toast type literals.
 */
export type NotificationVariant = 'success' | 'error' | 'info' | 'warning';
