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
  XboxMappingProgressEvent,
  XboxGameMappingsUpdatedEvent,
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
  createCompletionHandler,
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
  formatEpicGameMappingsUpdatedMessage,
  formatXboxMappingProgressMessage,
  formatXboxMappingCompleteMessage,
  formatXboxGameMappingsUpdatedMessage
} from './detailMessageFormatters';

/**
 * Terminal `DatabaseResetComplete` SignalR payload (camelCase, mirrors the backend
 * `SignalRNotifications.DatabaseResetComplete` record). Emitted exactly once via
 * `OperationInfo.OnTerminalEmit` on the normal success/error path AND the universal
 * force-kill/cancel path (PR2). Defined locally because the special-case handlers consume
 * it directly; it satisfies `createCompletionHandler`'s `{ success; stageKey?; context?;
 * message?; cancelled? }` generic constraint.
 */
interface DatabaseResetCompleteEvent {
  operationId: string;
  success: boolean;
  stageKey?: string;
  status?: string;
  cancelled?: boolean;
  error?: string;
  context?: Record<string, string | number | boolean>;
}

export interface SpecialCaseHandlers {
  handleDepotMappingStarted: (event: DepotMappingStartedEvent) => void;
  handleDepotMappingProgress: (event: DepotMappingProgressEvent) => void;
  handleDepotMappingComplete: (event: DepotMappingCompleteEvent) => void;
  handleDatabaseResetStarted: (event: DatabaseResetStartedEvent) => void;
  handleDatabaseResetProgress: (event: DatabaseResetProgressEvent) => void;
  handleDatabaseResetComplete: (event: DatabaseResetCompleteEvent) => void;
  handleEpicMappingProgress: (event: EpicMappingProgressEvent) => void;
  handleEpicGameMappingsUpdated: (event: EpicGameMappingsUpdatedEvent) => void;
  handleXboxMappingProgress: (event: XboxMappingProgressEvent) => void;
  handleXboxGameMappingsUpdated: (event: XboxGameMappingsUpdatedEvent) => void;
  handleSteamSessionError: (event: SteamSessionErrorEvent) => void;
}

/**
 * Creates all special-case notification handlers.
 * These are handlers that don't fit the standard registry pattern because they:
 * - Use a custom completion handler (depot mapping)
 * - Complete via a terminal event that is idempotent with a legacy progress-status
 *   completion (database reset)
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

  // ========== Database Reset (started + progress + terminal complete event) ==========
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

  // Terminal DatabaseResetComplete handler (PR2 emits this exactly once on the normal
  // success/error path AND the universal force-kill/cancel path). Reuses the canonical
  // createCompletionHandler factory. Idempotent by construction: the factory's immediate
  // completion path returns `prev` unchanged when the target slot is no longer 'running',
  // so if the legacy terminal progress tick already completed the notification this is a
  // safe no-op — and vice versa. A Complete arriving with no prior notification still
  // surfaces as a fast-created card. operationId is seeded into success/cancelled details
  // so a fast-created card keeps a working cancel button (Task 2 cancel safety).
  const handleDatabaseResetComplete = createCompletionHandler<DatabaseResetCompleteEvent>(
    {
      type: 'database_reset',
      getId: () => NOTIFICATION_IDS.DATABASE_RESET,
      storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
      getSuccessMessage: (e) =>
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t('signalr.dbReset.complete'),
      getSuccessDetails: (e) => ({ operationId: e.operationId }),
      getFailureMessage: (e) =>
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t('signalr.generic.failed'),
      getCancelledMessage: (e) =>
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t('signalr.dbReset.cancelled'),
      getCancelledDetails: (e) => ({ operationId: e.operationId })
    },
    setNotifications,
    scheduleAutoDismiss
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

  // ========== Xbox Game Mapping (progress only via createStatusAwareProgressHandler) ==========
  const handleXboxMappingProgress = createStatusAwareProgressHandler<XboxMappingProgressEvent>(
    {
      type: 'xbox_game_mapping',
      getId: () => NOTIFICATION_IDS.XBOX_GAME_MAPPING,
      storageKey: NOTIFICATION_STORAGE_KEYS.XBOX_GAME_MAPPING,
      getMessage: formatXboxMappingProgressMessage,
      getProgress: (e) => e.percentComplete || 0,
      getStatus: (e) =>
        e.status === 'completed' ? 'completed' : e.status === 'failed' ? 'failed' : undefined,
      getCompletedMessage: (e) =>
        e.cancelled ? i18n.t('signalr.xboxMapping.cancelled') : formatXboxMappingCompleteMessage(e),
      getErrorMessage: (e) => i18n.t(e.stageKey ?? 'signalr.xboxMapping.failed', e.context ?? {}),
      supportFastCompletion: true,
      getDetails: (e) => ({ operationId: e.operationId, cancelled: e.cancelled })
    },
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer
  );

  // ========== Xbox Game Mappings Updated ==========
  // Simple one-shot completion notification: no start/progress phases, just a completion event.
  // Only shows a notification when there are actual changes (new or updated games).
  const handleXboxGameMappingsUpdated = (event: XboxGameMappingsUpdatedEvent) => {
    if (!event.newGames && !event.updatedGames) return;

    const detailMessage = formatXboxGameMappingsUpdatedMessage(event);

    setNotifications((prev: UnifiedNotification[]) => {
      const filtered = prev.filter((n) => n.id !== NOTIFICATION_IDS.XBOX_GAME_MAPPING);
      const newNotification: UnifiedNotification = {
        id: NOTIFICATION_IDS.XBOX_GAME_MAPPING,
        type: 'xbox_game_mapping',
        status: 'completed',
        message: 'Xbox Games Updated',
        detailMessage,
        startedAt: new Date(),
        progress: 100,
        details: {
          totalXboxGames: event.totalGames,
          newXboxGames: event.newGames,
          updatedXboxGames: event.updatedGames
        }
      };
      return [...filtered, newNotification];
    });

    scheduleAutoDismiss(NOTIFICATION_IDS.XBOX_GAME_MAPPING);
  };

  // ========== Steam Session Error ==========
  // One-shot error display with auto-dismiss; uses fixed ID to prevent duplicates
  const handleSteamSessionError = (event: SteamSessionErrorEvent) => {
    const getSteamErrorTitle = (errorType: string): string => {
      switch (errorType) {
        case 'SessionReplaced':
        case 'LoggedInElsewhere':
          return i18n.t('signalr.steamSession.errorTitle.sessionReplaced');
        case 'AutoLogout':
          return i18n.t('signalr.steamSession.errorTitle.autoLogout');
        case 'InvalidCredentials':
        case 'AuthenticationRequired':
        case 'SessionExpired':
          return i18n.t('signalr.steamSession.errorTitle.authRequired');
        case 'ServerUnavailable':
        case 'ServiceUnavailable':
          return i18n.t('signalr.steamSession.errorTitle.serviceUnavailable');
        case 'RateLimited':
          return i18n.t('signalr.steamSession.errorTitle.rateLimited');
        default:
          return i18n.t('signalr.steamSession.errorTitle.generic');
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
    handleDatabaseResetComplete,
    handleEpicMappingProgress,
    handleEpicGameMappingsUpdated,
    handleXboxMappingProgress,
    handleXboxGameMappingsUpdated,
    handleSteamSessionError
  };
}
