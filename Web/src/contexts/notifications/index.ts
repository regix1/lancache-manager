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

// Type guards
export {
  isRunningNotification,
  isCompletedNotification,
  isFailedNotification,
  isDismissibleNotification,
  isCancelledNotification
} from './types';

// Constants
export {
  AUTO_DISMISS_DELAY_MS,
  CANCELLED_NOTIFICATION_DELAY_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  COMPLETION_ANIMATION_DELAY_MS,
  STEAM_ERROR_DISMISS_DELAY_MS,
  TOAST_DEFAULT_DURATION_MS,
  INCREMENTAL_SCAN_ANIMATION_STEPS,
  ANIMATION_STEP_DELAY_MS,
  INCREMENTAL_SCAN_ANIMATION_DURATION_MS,
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS
} from './constants';

// Handler factories (for advanced usage)
export {
  createStartedHandler,
  createProgressHandler,
  createCompletionHandler,
  createStatusAwareProgressHandler
} from './handlerFactories';

export type {
  StartedHandlerConfig,
  ProgressHandlerConfig,
  CompletionHandlerConfig,
  StatusAwareProgressConfig
} from './handlerFactories';

// Recovery factories (for advanced usage)
export {
  createSimpleRecoveryFunction,
  createDynamicRecoveryFunction,
  createCacheRemovalsRecoveryFunction,
  RECOVERY_CONFIGS
} from './recoveryFactory';

export type {
  SimpleRecoveryConfig,
  DynamicRecoveryConfig
} from './recoveryFactory';

// Detail message formatters
export {
  formatLogProcessingMessage,
  formatLogProcessingDetailMessage,
  formatLogProcessingCompletionMessage,
  formatFastProcessingCompletionMessage,
  formatDepotMappingDetailMessage,
  formatDepotMappingRecoveryDetailMessage,
  formatLogRemovalMessage,
  formatGameRemovalMessage,
  formatServiceRemovalMessage,
  formatCacheClearMessage,
  formatDatabaseResetMessage,
  formatLogProcessingRecoveryMessage,
  formatLogProcessingRecoveryDetailMessage
} from './detailMessageFormatters';
