// Main context provider
export { NotificationsProvider } from './NotificationsContext';

// Hook (re-exported for backward compatibility)
export { useNotifications } from './useNotifications';

// Types (only exporting what's used externally)
export type { UnifiedNotification } from './types';

// Constants (only exporting what's used externally)
export { NOTIFICATION_ANIMATION_DURATION_MS, NOTIFICATION_IDS } from './constants';

// Notification registry and handler hook (for external composition)
export { NOTIFICATION_REGISTRY } from './notificationRegistry';
export { useNotificationHandlers } from './useNotificationHandlers';
