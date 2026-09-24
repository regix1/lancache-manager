/**
 * Constants for the notification system: type maps, shared i18n keys and timing values.
 */

import type { NotificationType } from './types';

/**
 * Backend OperationType wire string (camelCase) -> notification type of the run card a run row
 * draws. A wire type absent here draws no card (statusCheck, cacheFileCount and
 * performanceOptimization); the server lists those same types as the ones without a card.
 */
export const OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE: Record<string, NotificationType> = {
  cacheClearing: 'cache_clearing',
  corruptionRemoval: 'corruption_removal',
  corruptionDetection: 'corruption_detection',
  gameDetection: 'game_detection',
  logProcessing: 'log_processing',
  gameRemoval: 'game_removal',
  serviceRemoval: 'service_removal',
  depotMapping: 'depot_mapping',
  dataImport: 'data_import',
  databaseReset: 'database_reset',
  logRemoval: 'log_removal',
  epicMapping: 'epic_game_mapping',
  xboxMapping: 'xbox_game_mapping',
  battleNetMapping: 'battle_net_game_mapping',
  riotMapping: 'riot_game_mapping',
  evictionScan: 'eviction_scan',
  evictionRemoval: 'eviction_removal',
  cacheSizeScan: 'cache_size_scan',
  scheduledPrefill: 'scheduled_prefill',
  logRotation: 'log_rotation',
  gameImageFetch: 'game_image_fetch',
  cacheSnapshot: 'cache_snapshot',
  operationHistoryCleanup: 'operation_history_cleanup',
  dashboardCacheWarmer: 'dashboard_cache_warmer',
  prefillLogin: 'prefill_login'
};

/**
 * Scheduled-run notification type -> the Schedules-page serviceKey that owns its display setting.
 * Closed list covering EVERY schedule card's run notifications: the buildScheduledRunEntry({ type })
 * calls in notificationRegistry.ts, the hand-wired scheduled_prefill entry, and the cards whose runs
 * ride an existing operation pipeline (their card notification type comes from the backend
 * ServiceScheduleRegistry serviceKey -> OperationType map, so a manual run of the same operation
 * shares the card and therefore the display setting). A notification whose type is absent here has
 * no per-card display setting and always renders as a full card. Keep in lockstep with the backend
 * registry: a new scheduled service adds one line here. The two catalog announcements are not
 * scheduled runs at all, but they report the result of one and so ride their mapping service's
 * display setting.
 */
export const SCHEDULED_NOTIFICATION_TYPE_TO_SERVICE_KEY: Partial<Record<NotificationType, string>> =
  {
    log_rotation: 'logRotation',
    game_image_fetch: 'gameImageFetch',
    cache_snapshot: 'cacheSnapshot',
    operation_history_cleanup: 'operationHistoryCleanup',
    dashboard_cache_warmer: 'dashboardCacheWarmer',
    scheduled_prefill: 'scheduledPrefill',
    eviction_scan: 'cacheReconciliation',
    cache_size_scan: 'cacheSizeScan',
    game_detection: 'gameDetection',
    depot_mapping: 'depotMapping',
    epic_game_mapping: 'epicMapping',
    epic_catalog_update: 'epicMapping',
    xbox_game_mapping: 'xboxMapping',
    xbox_catalog_update: 'xboxMapping',
    battle_net_game_mapping: 'battleNetMapping',
    riot_game_mapping: 'riotMapping'
  };

/**
 * Below this viewport width the notification bar renders at most this many full cards; the rest
 * auto-condense to thin lines so a burst of concurrent runs cannot bury the page on a phone.
 */
export const MOBILE_FULL_CARD_CAP = 3;

/**
 * Cancel state that lives ONLY in this browser session and that no server payload can know:
 * the X button's two-stage soft-cancel -> force-kill intent (`cancelRequested`/`cancelSent`,
 * written and read by the cancel handler in components/common/notificationCancel.ts) and the
 * bulk queue's cancel signal (`cancelling`, the only flag useBatchQueue's cascade honours).
 *
 * These are NOT `details.cancelled`, which is the TERMINAL outcome the server reports and which
 * renders the card in the neutral gray a stop earns rather than the red a failure does - see
 * cacheRemovalHelpers, which sets both at once as `{ cancelled: true, cancelling: false }`.
 *
 * They are the only part of a run card the browser writes: `updateNotification` keeps exactly
 * these keys from a run card's patch, and the server's rows own everything else.
 */
export const LIVE_ONLY_CANCEL_DETAIL_KEYS = [
  'cancelRequested',
  'cancelPending',
  'cancelSent',
  'cancelling'
] as const;

// ============================================================================
// Shared lifecycle values
// ============================================================================

/** Full progress shared by terminal notification cards and bulk-progress calculations. */
export const FULL_PROGRESS_PERCENT = 100;

/** Highest displayed progress for operations that have not emitted completion yet. */
export const ACTIVE_PROGRESS_PERCENT_CAP = 99.9;

/** Generic completion fallback shared by lifecycle handlers and message formatters. */
export const GENERIC_COMPLETION_I18N_KEY = 'signalr.generic.complete';

/** Generic failure fallback shared by lifecycle handlers and message formatters. */
export const GENERIC_FAILURE_I18N_KEY = 'signalr.generic.failed';

/** Generic cancellation fallback for an operation that stopped without its own message. */
export const GENERIC_CANCELLED_I18N_KEY = 'signalr.generic.cancelled';

/** Generic fallback for a run that was declined before it started and carried no reason. */
export const GENERIC_SKIPPED_I18N_KEY = 'signalr.generic.skipped';

/** Waiting-card message keys shared by live SignalR creation and REST recovery. */
export const OPERATION_WAITING_I18N_KEYS = {
  DEFAULT: 'common.notifications.operationWaiting',
  NAMED: 'common.notifications.operationWaitingNamed',
  BLOCKED: 'common.notifications.operationWaitingOn',
  NAMED_BLOCKED: 'common.notifications.operationWaitingOnNamed'
} as const;

/** Game-removal progress key shared by direct, bulk, and recovered removal cards. */
export const REMOVING_GAME_I18N_KEY = 'management.gameDetection.removingGame';

/** Game-removal failure fallback shared by direct and bulk removal flows. */
export const FAILED_TO_REMOVE_GAME_I18N_KEY = 'management.gameDetection.failedToRemoveGame';

// Window event names (including the notification system's own) live in one registry:
// APP_EVENTS in @utils/constants. A window event name is a contract between modules that never
// import each other, so keeping them all in one place is what makes a rename safe.

// ============================================================================
// Timing Constants
// ============================================================================

/**
 * The one popup time (5 seconds): how long a card that leaves on its own stays first - toasts,
 * error toasts, the catalog announcements, and a finished run card that is neither red nor amber.
 * Red and amber run cards stay until closed.
 */
export const AUTO_DISMISS_DELAY_MS = 5000;

/** Duration of notification slide/fade animations (300ms) */
export const NOTIFICATION_ANIMATION_DURATION_MS = 300;
