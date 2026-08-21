/**
 * Types for the unified notification system.
 * These types are used throughout the notification context, handlers, and UI components.
 */

import type { OperationStatus, NotificationVariant } from '../../types/operations';
import type { CorruptionDetectionMethod, CorruptionScanCoverage } from '../../types';
import type {
  StructuralBaselineStatus,
  StructuralEffectiveScanMode,
  StructuralScanMode
} from '../../types/corruptionScan';

/**
 * All possible notification types in the system.
 * Each type corresponds to a specific operation or event.
 */
export type NotificationType =
  | 'log_processing'
  | 'cache_clearing'
  | 'log_removal'
  | 'service_removal'
  | 'game_removal'
  | 'corruption_removal'
  | 'corruption_detection'
  | 'database_reset'
  | 'depot_mapping'
  | 'game_detection'
  | 'data_import'
  | 'epic_game_mapping'
  | 'xbox_game_mapping'
  | 'battle_net_game_mapping'
  | 'riot_game_mapping'
  | 'eviction_scan'
  | 'eviction_removal'
  | 'cache_size_scan'
  | 'scheduled_prefill'
  | 'log_rotation'
  | 'game_image_fetch'
  | 'cache_snapshot'
  | 'operation_history_cleanup'
  | 'performance_optimization'
  | 'dashboard_cache_warmer'
  | 'bulk_removal'
  | 'prefill_login'
  | 'steam_session_error'
  | 'generic';

/**
 * Possible states for a notification. Aligned with the canonical backend
 * `OperationStatus` so SignalR status fields can flow through unchanged.
 * Consumers that only care about the narrower "running | completed | failed"
 * triple continue to work because those three values are still members.
 */
export type NotificationStatus = OperationStatus;

/** Whether a running notification has a known progress denominator. */
export type NotificationProgressMode = 'determinate' | 'indeterminate';

/**
 * Unified notification data structure.
 * Represents all types of notifications in the system with a common interface.
 */
export interface UnifiedNotification {
  /** Unique identifier for this notification */
  id: string;
  /** The type of operation this notification represents */
  type: NotificationType;
  /** Current status of the operation */
  status: NotificationStatus;
  /** Progress percentage (0-100) for operations that support progress tracking */
  progress?: number;
  /** Explicit progress semantics; numeric progress remains for legacy determinate cards. */
  progressMode?: NotificationProgressMode;
  /** Text equivalent of the visible progress metrics for assistive technology. */
  progressAriaValueText?: string;
  /** Primary message displayed to the user */
  message: string;
  /** Secondary detail message with additional information */
  detailMessage?: string;
  /** Timestamp when the operation started */
  startedAt: Date;
  /**
   * Version counter for tracking notification instances.
   * Used to prevent race conditions with auto-dismiss timers.
   * When a notification is updated, this counter is incremented so that
   * stale auto-dismiss callbacks can detect they should not proceed.
   * Optional for backwards compatibility - defaults to 0 if not provided.
   */
  instanceVersion?: number;

  /** Type-specific details for the notification */
  details?: {
    // For log_processing
    mbProcessed?: number;
    mbTotal?: number;
    entriesProcessed?: number;
    estimatedTime?: string;

    // For cache_clearing
    filesDeleted?: number;
    directoriesProcessed?: number;
    bytesDeleted?: number;
    operationId?: string;
    /** First cancel click sent; second click force-kills. */
    cancelRequested?: boolean;
    /** Set after cancel/force-kill API call was invoked for this notification. */
    cancelSent?: boolean;
    cancelling?: boolean;

    // For service_removal
    service?: string;
    filesProcessed?: number;
    linesProcessed?: number;
    linesRemoved?: number;

    // For game_removal
    gameAppId?: number;
    gameName?: string;
    epicAppId?: string;
    steamAppId?: string;
    bytesFreed?: number;
    logEntriesRemoved?: number;

    // For depot_mapping
    totalMappings?: number;
    processedMappings?: number;
    mappingsApplied?: number;
    percentComplete?: number;
    isProcessing?: boolean;
    isLoggedOn?: boolean;
    downloadsUpdated?: number;

    // For game_detection
    scanType?: 'full' | 'incremental';
    totalGamesDetected?: number;
    totalServicesDetected?: number;

    // For corruption detection
    detectionMethod?: CorruptionDetectionMethod;
    scanMode?: StructuralScanMode;
    effectiveScanMode?: StructuralEffectiveScanMode;
    baselineStatus?: StructuralBaselineStatus;
    resumed?: boolean;
    filesDiscovered?: number;
    filesReused?: number;
    filesInspected?: number;
    filesRevalidated?: number;
    invalidFiles?: number;
    filesPendingRetry?: number;
    filesPruned?: number;
    stateEntries?: number;
    stateCommitted?: boolean;
    detectionCounts?: Record<string, number>;
    coverage?: CorruptionScanCoverage | null;

    // For epic_game_mapping
    totalEpicGames?: number;
    newEpicGames?: number;
    updatedEpicGames?: number;

    // For xbox_game_mapping
    newXboxGames?: number;
    newXboxPatterns?: number;

    // For generic notifications
    notificationType?: NotificationVariant;
    /**
     * Schedules-page serviceKey that owns this toast (e.g. the Run Now acknowledgment), so the
     * card's notification style applies to it the same as to the run's own lifecycle events.
     */
    serviceKey?: string;

    // For data_import
    recordsImported?: number;
    recordsSkipped?: number;
    recordsErrors?: number;
    totalRecords?: number;

    // Cancellation flag
    cancelled?: boolean;
  };

  /** Error message when status is 'failed' */
  error?: string;
}

/**
 * Context type for the notifications provider.
 * Provides access to notifications state and mutation functions.
 */
export interface NotificationsContextType {
  /** Array of all current notifications */
  notifications: UnifiedNotification[];
  /**
   * Adds a new notification to the system.
   * @param notification - The notification data (id and startedAt are generated automatically)
   * @returns The generated notification ID
   */
  addNotification: (notification: Omit<UnifiedNotification, 'id' | 'startedAt'>) => string;
  /**
   * Updates an existing notification.
   * @param id - The notification ID to update
   * @param updates - Partial notification data to merge
   */
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void;
  /**
   * Removes a notification immediately without animation.
   * @param id - The notification ID to remove
   */
  removeNotification: (id: string) => void;
  /**
   * Removes all completed or failed notifications.
   */
  clearCompletedNotifications: () => void;
  /**
   * Returns true if any removal operation is currently running.
   * Used to disable all removal buttons since they share a backend lock.
   */
  isAnyRemovalRunning: boolean;
  /**
   * Returns the type of removal currently running, or null if none.
   */
  activeRemovalType: NotificationType | null;
  /**
   * Schedules a notification to auto-dismiss after the configured delay.
   * Respects the user's "Keep Notifications Visible" preference (no-op when enabled).
   * Used by caller-managed notifications (e.g. useCancellableQueue's bulk_removal)
   * that don't go through a registry handler and therefore don't get auto-dismiss
   * scheduled for them automatically.
   */
  scheduleAutoDismiss: (notificationId: string, delayMs?: number) => void;
}

// ============================================================================
// Handler Factory Types
// ============================================================================

/** React setState dispatch function for notifications */
export type SetNotifications = React.Dispatch<React.SetStateAction<UnifiedNotification[]>>;

/**
 * Function to schedule automatic dismissal of a notification.
 * @param notificationId - The notification ID to dismiss
 * @param delayMs - Optional delay in milliseconds before dismissal
 */
export type ScheduleAutoDismiss = (notificationId: string, delayMs?: number) => void;

/**
 * Function to cancel a pending auto-dismiss timer.
 * @param notificationId - The notification ID whose timer should be cancelled
 */
export type CancelAutoDismissTimer = (notificationId: string) => void;

/**
 * Function to remove a notification by ID.
 * @param notificationId - The notification ID to remove
 */
export type RemoveNotification = (notificationId: string) => void;

// ============================================================================
// Notification Registry Types
// ============================================================================

/**
 * The SignalR payload a lifecycle config reads. Entries that name their concrete
 * event interface get every field checked; the handler plumbing, which forwards
 * configs without knowing the type, uses the `any` default.
 */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type LifecycleEvent = any;

/**
 * Configuration for a started event handler within a registry entry.
 */
export interface RegistryStartedConfig<TEvent = LifecycleEvent> {
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: TEvent) => boolean;
  /** Default message shown when the operation starts */
  defaultMessage: string;
  /** Optional function to get a custom message from the event */
  getMessage?: (event: TEvent) => string;
  /** Optional function to get notification details from the event */
  getDetails?: (event: TEvent) => UnifiedNotification['details'];
  /** If true, always replace existing notification (for restartable operations) */
  replaceExisting?: boolean;
  /**
   * Progress semantics for the card this event opens. A started card is built with
   * `progress: 0`, which reads as determinate and freezes the bar at zero for the whole
   * wait; an operation that starts by waiting on a person sets 'indeterminate' so the
   * card sweeps until real progress arrives.
   */
  progressMode?: UnifiedNotification['progressMode'];
  /** Extra notification ids to remove when this operation starts (migration/cleanup) */
  additionalIdsToRemove?: string[];
}

/**
 * Configuration for a progress event handler within a registry entry.
 */
export interface RegistryProgressConfig<TEvent = LifecycleEvent> {
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: TEvent) => boolean;
  /** Function to get the progress message from the event */
  getMessage: (event: TEvent) => string;
  /** Function to get progress percentage (0-100) from the event */
  getProgress: (event: TEvent) => number;
  /** Optional secondary progress metrics shown below the stable primary message. */
  getDetailMessage?: (event: TEvent) => string | undefined;
  /** Optional determinate/indeterminate override for phase-aware operations. */
  getProgressMode?: (event: TEvent) => NotificationProgressMode | undefined;
  /** Optional textual equivalent of the progress state. */
  getProgressAriaValueText?: (event: TEvent) => string | undefined;
  /** Function to get the status from the event */
  getStatus: (event: TEvent) => string | undefined;
  /** Message to show on completion */
  getCompletedMessage?: (event: TEvent) => string;
  /** Message to show on error */
  getErrorMessage?: (event: TEvent) => string | undefined;
  /** If true, support fast completion */
  supportFastCompletion?: boolean;
  /** Optional function to get notification details from the event */
  getDetails?: (event: TEvent) => UnifiedNotification['details'];
}

/**
 * Configuration for a completion event handler within a registry entry.
 */
export interface RegistryCompleteConfig<TEvent = LifecycleEvent> {
  /** Optional gate that suppresses and removes the notification for this event */
  shouldDisplay?: (event: TEvent) => boolean;
  /** Optional function to get the success message */
  getSuccessMessage?: (event: TEvent, existing?: UnifiedNotification) => string;
  /** Optional function to get success details */
  getSuccessDetails?: (
    event: TEvent,
    existing?: UnifiedNotification
  ) => UnifiedNotification['details'];
  /** Optional function to get the cancelled message */
  getCancelledMessage?: (event: TEvent, existing?: UnifiedNotification) => string;
  /** Optional function to get cancelled details */
  getCancelledDetails?: (
    event: TEvent,
    existing?: UnifiedNotification
  ) => UnifiedNotification['details'];
  /**
   * Optional function to get detail message (shown below main message). May return undefined, in
   * which case the card KEEPS its existing detail line - the completion handler falls back with
   * `?? n.detailMessage` - so a formatter with nothing to say cannot blank a useful line.
   */
  getDetailMessage?: (event: TEvent) => string | undefined;
  /** Optional function to get the failure message */
  getFailureMessage?: (event: TEvent) => string;
  /** If true, show a brief animation delay before marking complete */
  useAnimationDelay?: boolean;
  /** Optional function to get ID for fast completion (if different from getId) */
  getFastCompletionId?: () => string;
}

/**
 * How a notification type's SignalR lifecycle handlers are wired.
 *   - 'standard': the {@link useNotificationHandlers} loop subscribes
 *     started/progress/complete handlers built from this entry's configs.
 *   - 'special': the entry is metadata-only (cancelKind + recovery). Its
 *     SignalR handlers are hand-built in `createSpecialCaseHandlers` and wired
 *     via SPECIAL_NOTIFICATION_CONTRACTS. The standard loop MUST skip these to
 *     avoid double-subscribing.
 */
export type NotificationWiring = 'standard' | 'special';

/**
 * How the X button cancels a notification.
 *   - 'serverOp': cancellable server operation (soft-cancel → force-kill, with
 *     a deferred watchdog when the operationId hasn't arrived yet).
 *   - 'clientQueue': client-side bulk queue (flag flip only; the
 *     BulkRemovalProvider's always-mounted cascade effect performs the cancel).
 *   - 'none': not cancellable (no X button shown).
 */
export type CancelKind = 'serverOp' | 'clientQueue' | 'none';

/**
 * Simple recovery: a single GET to a per-type status endpoint that either
 * re-seeds a running card, skips (silent self-heal), or stale-completes a stuck
 * running card. The 10 former RECOVERY_CONFIGS entries map onto this shape.
 *
 * The generic `TData` is the REST response DTO. `isProcessing`/`shouldSkip`/
 * `createNotification` read REST snake_case/camelCase fields directly and MUST
 * NOT be normalized against the SignalR event property names (a field can cross
 * both boundaries with different casing).
 */
export type StageContext = Record<string, string | number | boolean | null>;

export type RecoveryTranslationValidation =
  | {
      kind: 'stageKey';
      cases: readonly { stageKey: string; context: StageContext }[];
    }
  | { kind: 'dedicated' };

export interface SimpleRecoveryConfig<TData = unknown> {
  kind: 'simple';
  /** Data-driven classification consumed by validate-stage-keys.mjs. */
  translationValidation: RecoveryTranslationValidation;
  apiEndpoint: string;
  isProcessing: (data: TData) => boolean;
  shouldSkip?: (data: TData) => boolean;
  createNotification: (
    data: TData
  ) => Omit<UnifiedNotification, 'id' | 'type' | 'status' | 'startedAt'>;
  /**
   * Key rather than text: the registry is a module-level literal, so a translated string here
   * would freeze at import time and survive a language change.
   */
  staleMessageKey: string;
}

/**
 * Marker recovery: this type is served by the single
 * `/api/cache/removals/active` batch fetch (one GET covering game_removal,
 * service_removal, corruption_removal, eviction_removal). The runner issues the
 * batch fetch exactly once for the whole group.
 */
interface CacheRemovalsBatchRecoveryConfig {
  kind: 'cacheRemovalsBatch';
}

/** No recovery (special toasts / types with no status endpoint). */
interface NoRecoveryConfig {
  kind: 'none';
}

/** Discriminated recovery union for a registry entry. */
export type RecoveryConfig =
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  SimpleRecoveryConfig<any> | CacheRemovalsBatchRecoveryConfig | NoRecoveryConfig;

/**
 * Cancel wiring for a registry entry, as a type-level constraint that pairs
 * `cancelKind` with `cancelTooltipKey`:
 *   - `cancelKind: 'none'` → no tooltip key (the X button is never shown).
 *   - any other `cancelKind` → `cancelTooltipKey` is REQUIRED, so a cancellable
 *     entry can never compile without the tooltip key that
 *     `UniversalNotificationBar` needs to render its cancel button.
 *
 * `allowsDeferredCancel` (read for 'serverOp' only) says this type's first
 * operationId can arrive on a PROGRESS event, so a click on a running card that
 * has no id yet is remembered and sent by the bar's watchdog once the id lands.
 * A type whose only id-bearing event is a Started event cannot do that -
 * mergeEventDetails strips the cancel flags at the moment that id arrives - so
 * its cards show no X until they carry an operationId.
 */
type CancelWiring =
  | { cancelKind: 'none'; cancelTooltipKey?: never; allowsDeferredCancel?: never }
  | {
      cancelKind: Exclude<CancelKind, 'none'>;
      cancelTooltipKey: string;
      allowsDeferredCancel?: boolean;
    };

/**
 * Declarative registry entry describing the full lifecycle of a notification type.
 * Each entry specifies the started, progress, and completion handler configs
 * along with the SignalR event names they map to, plus cancel + recovery wiring.
 */
export type NotificationRegistryEntry = CancelWiring & {
  /** The notification type */
  type: NotificationType;
  /** Singleton notification ID */
  id: string;
  /** localStorage persistence key */
  storageKey: string;
  /**
   * How this entry's SignalR handlers are wired. 'special' entries are
   * metadata-only (no `events`/`started`/`progress`); the standard handler loop
   * skips them and they are subscribed by createSpecialCaseHandlers instead.
   */
  wiring: NotificationWiring;
  /** Recovery wiring (discriminated union). */
  recovery: RecoveryConfig;
  /**
   * SignalR event names for each lifecycle phase. Present only for
   * wiring:'standard' entries (the loop reads them).
   */
  events?: {
    started: string;
    progress: string;
    complete: string;
  };
  /** Configuration for the started handler (standard entries only) */
  started?: RegistryStartedConfig;
  /** Configuration for the progress handler (standard entries only) */
  progress?: RegistryProgressConfig;
  /** Configuration for the completion handler (optional for types without a separate complete event) */
  complete?: RegistryCompleteConfig;
  /** Optional callback invoked after the completion handler runs (e.g., to remove related notifications) */
  onComplete?: (removeNotification: RemoveNotification) => void;
};
