/**
 * Special-case SignalR notification handlers that don't fit the standard
 * Started->Progress->Complete registry pattern.
 *
 * These handlers are extracted from NotificationsContext.tsx to keep the
 * component file focused on React concerns only.
 */

import type {
  DepotMappingStartedEvent,
  DepotMappingProgressEvent,
  DepotMappingCompleteEvent,
  DatabaseResetStartedEvent,
  DatabaseResetProgressEvent,
  EpicMappingProgressEvent,
  EpicGameMappingsUpdatedEvent,
  SteamSessionErrorEvent
} from '../SignalRContext/types';

import type {
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss,
  CancelAutoDismissTimer
} from './types';
import {
  STEAM_ERROR_DISMISS_DELAY_MS,
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS
} from './constants';
import {
  createStartedHandler,
  createStatusAwareProgressHandler,
  createDepotMappingCompletionHandler
} from './handlerFactories';
import i18n from '@/i18n';
import {
  formatDatabaseResetProgressMessage,
  formatDatabaseResetCompleteMessage,
  formatDepotMappingStartedMessage,
  formatDepotMappingProgressMessage,
  formatEpicMappingProgressMessage,
  formatEpicMappingCompleteMessage,
  formatEpicGameMappingsUpdatedMessage
} from './detailMessageFormatters';

interface SpecialCaseHandlers {
  handleDepotMappingStarted: (event: DepotMappingStartedEvent) => void;
  handleDepotMappingProgress: (event: DepotMappingProgressEvent) => void;
  handleDepotMappingComplete: (event: DepotMappingCompleteEvent) => void;
  handleDatabaseResetStarted: (event: DatabaseResetStartedEvent) => void;
  handleDatabaseResetProgress: (event: DatabaseResetProgressEvent) => void;
  handleEpicMappingProgress: (event: EpicMappingProgressEvent) => void;
  handleEpicGameMappingsUpdated: (event: EpicGameMappingsUpdatedEvent) => void;
  handleSteamSessionError: (event: SteamSessionErrorEvent) => void;
}

/**
 * Creates all special-case notification handlers.
 * These are handlers that don't fit the standard registry pattern because they:
 * - Use a custom completion handler (depot mapping)
 * - Have no completion event (database reset)
 * - Have only progress events (epic game mapping)
 * - Are one-shot custom handlers (EpicGameMappingsUpdated, SteamSessionError)
 */
export function createSpecialCaseHandlers(
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer
): SpecialCaseHandlers {
  // ========== Depot Mapping (uses special createDepotMappingCompletionHandler) ==========
  const handleDepotMappingStarted = createStartedHandler<DepotMappingStartedEvent>(
    {
      type: 'depot_mapping',
      getId: () => NOTIFICATION_IDS.DEPOT_MAPPING,
      storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
      defaultMessage: 'Starting depot mapping scan...',
      getMessage: formatDepotMappingStartedMessage,
      getDetails: (e) => ({ operationId: e.operationId, isLoggedOn: e.isLoggedOn }),
      replaceExisting: true // Depot mapping can be restarted
    },
    setNotifications,
    cancelAutoDismissTimer
  );

  const handleDepotMappingProgress = createStatusAwareProgressHandler<DepotMappingProgressEvent>(
    {
      type: 'depot_mapping',
      getId: () => NOTIFICATION_IDS.DEPOT_MAPPING,
      storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
      getMessage: (event) => formatDepotMappingProgressMessage(event, undefined),
      getProgress: (event) => event.percentComplete ?? event.progressPercent ?? 0,
      getStatus: (e) => {
        if (e.status === 'completed') return 'completed';
        if (e.status === 'failed') return 'failed';
        return undefined;
      },
      getCompletedMessage: (e) =>
        i18n.t(e.stageKey ?? 'signalr.depotMapping.finalized', e.context ?? {}),
      getErrorMessage: (e) => i18n.t(e.stageKey ?? 'signalr.generic.failed', e.context ?? {}),
      getDetails: (e) => ({ operationId: e.operationId })
    },
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer
  );

  const handleDepotMappingComplete = createDepotMappingCompletionHandler(
    setNotifications,
    scheduleAutoDismiss
  );

  // ========== Database Reset (only started + progress, no complete event) ==========
  const handleDatabaseResetStarted = createStartedHandler<DatabaseResetStartedEvent>(
    {
      type: 'database_reset',
      getId: () => NOTIFICATION_IDS.DATABASE_RESET,
      storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
      defaultMessage: 'Starting database reset...',
      getMessage: (e) => i18n.t(e.stageKey ?? 'signalr.dbReset.starting', e.context ?? {}),
      getDetails: (e) => ({ operationId: e.operationId })
    },
    setNotifications,
    cancelAutoDismissTimer
  );

  const handleDatabaseResetProgress = createStatusAwareProgressHandler<DatabaseResetProgressEvent>(
    {
      type: 'database_reset',
      getId: () => NOTIFICATION_IDS.DATABASE_RESET,
      storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
      getMessage: formatDatabaseResetProgressMessage,
      getProgress: (e) => e.percentComplete || 0,
      getStatus: (e) =>
        e.status === 'completed'
          ? 'completed'
          : e.status === 'failed' || e.status === 'cancelled'
            ? 'failed'
            : undefined,
      getCompletedMessage: formatDatabaseResetCompleteMessage,
      getErrorMessage: (e) =>
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t('signalr.generic.failed'),
      supportFastCompletion: true,
      getDetails: (e) => ({ operationId: e.operationId })
    },
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer
  );

  // ========== Epic Game Mapping (progress only via createStatusAwareProgressHandler) ==========
  const handleEpicMappingProgress = createStatusAwareProgressHandler<EpicMappingProgressEvent>(
    {
      type: 'epic_game_mapping',
      getId: () => NOTIFICATION_IDS.EPIC_GAME_MAPPING,
      storageKey: NOTIFICATION_STORAGE_KEYS.EPIC_GAME_MAPPING,
      getMessage: formatEpicMappingProgressMessage,
      getProgress: (e) => e.percentComplete || 0,
      getStatus: (e) =>
        e.status === 'completed' ? 'completed' : e.status === 'failed' ? 'failed' : undefined,
      getCompletedMessage: (e) =>
        e.cancelled ? i18n.t('signalr.epicMapping.cancelled') : formatEpicMappingCompleteMessage(e),
      getErrorMessage: (e) => i18n.t(e.stageKey ?? 'signalr.epicMapping.failed', e.context ?? {}),
      supportFastCompletion: true,
      getDetails: (e) => ({ operationId: e.operationId, cancelled: e.cancelled })
    },
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer
  );

  // ========== Epic Game Mappings Updated ==========
  // Simple one-shot completion notification: no start/progress phases, just a completion event.
  // Only shows a notification when there are actual changes (new or updated games).
  const handleEpicGameMappingsUpdated = (event: EpicGameMappingsUpdatedEvent) => {
    if (!event.newGames && !event.updatedGames) return;

    const detailMessage = formatEpicGameMappingsUpdatedMessage(event);

    setNotifications((prev: UnifiedNotification[]) => {
      const filtered = prev.filter((n) => n.id !== NOTIFICATION_IDS.EPIC_GAME_MAPPING);
      const newNotification: UnifiedNotification = {
        id: NOTIFICATION_IDS.EPIC_GAME_MAPPING,
        type: 'epic_game_mapping',
        status: 'completed',
        message: 'Epic Games Updated',
        detailMessage,
        startedAt: new Date(),
        progress: 100,
        details: {
          totalEpicGames: event.totalGames,
          newEpicGames: event.newGames,
          updatedEpicGames: event.updatedGames
        }
      };
      return [...filtered, newNotification];
    });

    scheduleAutoDismiss(NOTIFICATION_IDS.EPIC_GAME_MAPPING);
  };

  // ========== Steam Session Error ==========
  // One-shot error display with auto-dismiss; uses fixed ID to prevent duplicates
  const handleSteamSessionError = (event: SteamSessionErrorEvent) => {
    const getSteamErrorTitle = (errorType: string): string => {
      switch (errorType) {
        case 'SessionReplaced':
        case 'LoggedInElsewhere':
          return 'Steam Session Replaced';
        case 'AutoLogout':
          return 'Steam Auto-Logout';
        case 'InvalidCredentials':
        case 'AuthenticationRequired':
        case 'SessionExpired':
          return 'Steam Authentication Required';
        case 'ServerUnavailable':
        case 'ServiceUnavailable':
          return 'Steam Service Unavailable';
        case 'RateLimited':
          return 'Steam Rate Limited';
        default:
          return 'Steam Error';
      }
    };

    let shouldScheduleDismiss = false;

    setNotifications((prev: UnifiedNotification[]) => {
      const existingNotification = prev.find((n) => n.id === NOTIFICATION_IDS.STEAM_SESSION_ERROR);

      if (existingNotification) {
        const timeSinceCreation = Date.now() - existingNotification.startedAt.getTime();
        if (timeSinceCreation < 2000) {
          return prev;
        }
      }

      const newNotification: UnifiedNotification = {
        type: 'generic',
        status: 'failed',
        message: getSteamErrorTitle(event.errorType),
        detailMessage: event.stageKey
          ? i18n.t(event.stageKey, event.context ?? {})
          : i18n.t('signalr.steamSession.disconnected', { result: event.result ?? 'Unknown' }),
        details: {
          notificationType: 'error'
        },
        id: NOTIFICATION_IDS.STEAM_SESSION_ERROR,
        startedAt: new Date()
      };

      const filtered = prev.filter((n) => n.id !== NOTIFICATION_IDS.STEAM_SESSION_ERROR);
      shouldScheduleDismiss = true;
      return [...filtered, newNotification];
    });

    if (shouldScheduleDismiss) {
      scheduleAutoDismiss(NOTIFICATION_IDS.STEAM_SESSION_ERROR, STEAM_ERROR_DISMISS_DELAY_MS);
    }
  };

  return {
    handleDepotMappingStarted,
    handleDepotMappingProgress,
    handleDepotMappingComplete,
    handleDatabaseResetStarted,
    handleDatabaseResetProgress,
    handleEpicMappingProgress,
    handleEpicGameMappingsUpdated,
    handleSteamSessionError
  };
}
