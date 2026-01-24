// Main context and hook
export { NotificationsProvider, useNotifications } from './NotificationsContext';

// Types
export type {
  NotificationType,
  NotificationStatus,
  UnifiedNotification,
  NotificationsContextType,
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer,
  UpdateNotification
} from './types';

// Constants (only exporting what's used externally)
export { NOTIFICATION_ANIMATION_DURATION_MS } from './constants';
