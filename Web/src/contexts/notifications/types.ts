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
  | 'epic_catalog_update'
  | 'xbox_game_mapping'
  | 'xbox_catalog_update'
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
    /**
     * Notification types this bulk card's own items emit (a cache batch mixing
     * game and service items lists both). While the card runs, a per-item card of
     * one of these types is skipped so each item does not add a second card
     * beside the batch card. A batch whose items emit a type not in this list -
     * or any other running batch - never suppresses it.
     */
    itemTypes?: NotificationType[];
    /**
     * Operation id of the batch item currently in flight, parked or running. Item types are
     * shared - two batches, or a batch and a removal started from a page, can produce the same
     * one - so this is what proves a queue event belongs to THIS batch rather than merely
     * matching a type it declared. Cleared between items. It is deliberately not
     * `details.operationId`: that field puts the card's X on the server-cancel path, and a
     * batch card cancels its run through the client queue instead.
     */
    currentOperationId?: string;
    /**
     * True while the batch has sent its current item's request and has not learned that item's
     * operation id yet. The queue announces a parked operation from inside the request, so the
     * push can reach the card before the response does, and for that one round trip an empty
     * `currentOperationId` still means "this could be mine". Once the request is answered an
     * empty id means the batch has nothing to claim.
     */
    itemRequestPending?: boolean;

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
   * @param updates - Partial notification data to merge, or a function handed the live
   * notification that returns it. Use the function form for a read-modify-write of a nested
   * object such as `details`, which merges only at the top level: it reads the card React is
   * about to update rather than a snapshot taken before the caller's own earlier writes landed.
   */
  updateNotification: (
    id: string,
    updates:
      | Partial<UnifiedNotification>
      | ((notification: UnifiedNotification) => Partial<UnifiedNotification>)
  ) => void;
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
   * Used by caller-managed notifications (e.g. useBatchQueue's bulk_removal)
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
  getProgress: (event: TEvent) => number | undefined;
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
  /**
   * Fixed outcome for an entry whose single event IS the whole lifecycle. Such a payload reports
   * no `success` field, because there was no run to report on, so the entry states what its event
   * always means. Setting it also lets the event replace a terminal card in its slot: with no
   * started or progress phase there is no second operation of this type to confuse the card with,
   * and whatever sits there can only be an older announcement of the same kind.
   */
  succeeded?: boolean;
  /** Auto-dismiss delay for this type's terminal card, where the shared default is wrong. */
  dismissDelayMs?: number;
  /** If true, show a brief animation delay before marking complete */
  useAnimationDelay?: boolean;
}

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

type RecoveryTranslationValidation =
  | {
      kind: 'stageKey';
      cases: readonly { stageKey: string; context: StageContext }[];
    }
  | { kind: 'dedicated' };

interface SimpleRecoveryBase<TData> {
  kind: 'simple';
  /** Data-driven classification consumed by validate-stage-keys.mjs. */
  translationValidation: RecoveryTranslationValidation;
  apiEndpoint: string;
  isProcessing: (data: TData) => boolean;
  shouldSkip?: (data: TData) => boolean;
  /**
   * Key rather than text: the registry is a module-level literal, so a translated string here
   * would freeze at import time and survive a language change.
   */
  staleMessageKey: string;
}

/**
 * How many cards this type's status endpoint rebuilds. A singleton type re-seeds its one card
 * from `createNotification`; a type that owns one card per entity names them itself in
 * `recoverCards`, ids included, because the response is what knows which entities are running.
 */
export type SimpleRecoveryConfig<TData = unknown> = SimpleRecoveryBase<TData> &
  (
    | {
        createNotification: (
          data: TData
        ) => Omit<UnifiedNotification, 'id' | 'type' | 'status' | 'startedAt'>;
        recoverCards?: never;
      }
    | {
        createNotification?: never;
        recoverCards: (data: TData) => Omit<UnifiedNotification, 'type' | 'status' | 'startedAt'>[];
      }
  );

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
  /**
   * Card id for THIS event, for a type that owns one live card per entity rather than a
   * singleton. Left unset, every event of the type lands on {@link id}, which is what the
   * other entries rely on. Setting it also makes the type's storage key hold a record of
   * card id to card, so one entity's card can be cleared without destroying its siblings.
   */
  getId?: (event: unknown) => string;
  /** localStorage persistence key */
  storageKey: string;
  /** Recovery wiring (discriminated union). */
  recovery: RecoveryConfig;
  /**
   * SignalR event names for the lifecycle phases this type actually has. An entry
   * that declares none is metadata-only (cancel + recovery); the handler loop skips
   * it and its card is created by client code instead. An entry whose single event
   * carries the whole lifecycle declares only `complete`.
   */
  events?: {
    started?: string;
    progress?: string;
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
