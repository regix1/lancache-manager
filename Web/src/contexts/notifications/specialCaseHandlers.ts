/**
 * Special-case SignalR notification handlers that don't fit the standard
 * Started->Progress->Complete registry pattern.
 *
 * These handlers are extracted from NotificationsContext.tsx to keep the
 * component file focused on React concerns only.
 */

import type {
  DatabaseResetStartedEvent,
  DatabaseResetProgressEvent,
  EpicGameMappingsUpdatedEvent,
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
  FULL_PROGRESS_PERCENT,
  GENERIC_FAILURE_I18N_KEY,
  STEAM_ERROR_DISMISS_DELAY_MS,
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS
} from './constants';
import {
  createStartedHandler,
  createStatusAwareProgressHandler,
  createCompletionHandler
} from './handlers';
import i18n from '@/i18n';
import {
  formatDatabaseResetProgressMessage,
  formatDatabaseResetCompleteMessage,
  formatEpicGameMappingsUpdatedMessage,
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
  handleDatabaseResetStarted: (event: DatabaseResetStartedEvent) => void;
  handleDatabaseResetProgress: (event: DatabaseResetProgressEvent) => void;
  handleDatabaseResetComplete: (event: DatabaseResetCompleteEvent) => void;
  handleEpicGameMappingsUpdated: (event: EpicGameMappingsUpdatedEvent) => void;
  handleXboxGameMappingsUpdated: (event: XboxGameMappingsUpdatedEvent) => void;
  handleSteamSessionError: (event: SteamSessionErrorEvent) => void;
}

/**
 * Creates all special-case notification handlers.
 * These are handlers that don't fit the standard registry pattern because they:
 * - Complete via a terminal event that is idempotent with a legacy progress-status
 *   completion (database reset)
 * - Are one-shot custom handlers (mapping data updates, SteamSessionError)
 */
export function createSpecialCaseHandlers(
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  cancelAutoDismissTimer: CancelAutoDismissTimer
): SpecialCaseHandlers {
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
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t(GENERIC_FAILURE_I18N_KEY),
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
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t(GENERIC_FAILURE_I18N_KEY),
      getCancelledMessage: (e) =>
        e.stageKey ? i18n.t(e.stageKey, e.context ?? {}) : i18n.t('signalr.dbReset.cancelled'),
      getCancelledDetails: (e) => ({ operationId: e.operationId })
    },
    setNotifications,
    scheduleAutoDismiss
  );

  // ========== Epic Game Mappings Updated ==========
  // Simple one-shot completion notification: no start/progress phases, just a completion event.
  // Only shows a notification when there are actual changes (new or updated games).
  const handleEpicGameMappingsUpdated = (event: EpicGameMappingsUpdatedEvent) => {
    if (!event.newGames && !event.updatedGames) return;

    const detailMessage = formatEpicGameMappingsUpdatedMessage(event);

    setNotifications((prev: UnifiedNotification[]) => {
      const filtered = prev.filter((n) => n.id !== NOTIFICATION_IDS.EPIC_GAME_MAPPING_UPDATE);
      const newNotification: UnifiedNotification = {
        id: NOTIFICATION_IDS.EPIC_GAME_MAPPING_UPDATE,
        type: 'epic_game_mapping',
        status: 'completed',
        message: i18n.t('notifications.epicGameMappingsUpdated.title'),
        detailMessage,
        startedAt: new Date(),
        progress: FULL_PROGRESS_PERCENT,
        details: {
          totalEpicGames: event.totalGames,
          newEpicGames: event.newGames,
          updatedEpicGames: event.updatedGames
        }
      };
      return [...filtered, newNotification];
    });

    scheduleAutoDismiss(NOTIFICATION_IDS.EPIC_GAME_MAPPING_UPDATE);
  };

  // ========== Xbox Game Mappings Updated ==========
  // Simple one-shot completion notification: no start/progress phases, just a completion event.
  // The backend only emits this when the catalog merge actually persisted something, so a toast
  // shows when new games (newMappings) or new CDN fragments (newPatterns) were discovered.
  const handleXboxGameMappingsUpdated = (event: XboxGameMappingsUpdatedEvent) => {
    if (!event.newMappings && !event.newPatterns) return;

    const detailMessage = formatXboxGameMappingsUpdatedMessage(event);

    setNotifications((prev: UnifiedNotification[]) => {
      const filtered = prev.filter((n) => n.id !== NOTIFICATION_IDS.XBOX_GAME_MAPPING_UPDATE);
      const newNotification: UnifiedNotification = {
        id: NOTIFICATION_IDS.XBOX_GAME_MAPPING_UPDATE,
        type: 'xbox_game_mapping',
        status: 'completed',
        message: i18n.t('notifications.xboxGameMappingsUpdated.title'),
        detailMessage,
        startedAt: new Date(),
        progress: FULL_PROGRESS_PERCENT,
        details: {
          newXboxGames: event.newMappings,
          newXboxPatterns: event.newPatterns
        }
      };
      return [...filtered, newNotification];
    });

    scheduleAutoDismiss(NOTIFICATION_IDS.XBOX_GAME_MAPPING_UPDATE);
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
          : i18n.t('signalr.steamSession.disconnected', {
              result: event.result ?? i18n.t('common.unknown')
            }),
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
    handleDatabaseResetStarted,
    handleDatabaseResetProgress,
    handleDatabaseResetComplete,
    handleEpicGameMappingsUpdated,
    handleXboxGameMappingsUpdated,
    handleSteamSessionError
  };
}
