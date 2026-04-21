import {
  useCallback,
  useEffect,
  type Dispatch,
  type MutableRefObject,
  type SetStateAction
} from 'react';
import ApiService from '@services/api.service';
import { NOTIFICATION_IDS } from '@contexts/notifications';
import type { TFunction } from 'i18next';
import type { NotificationsContextType, UnifiedNotification } from '@contexts/notifications/types';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';
import {
  pruneGamesByCompletedRemovalNotifications,
  pruneGamesByRemovalTarget,
  pruneServicesByCompletedRemovalNotifications,
  pruneServicesByRemovalTarget,
  type CacheRemovalTarget
} from './cacheDetectionData';
import { FULL_REMOVAL_REFRESH_DELAY_MS } from './cacheEntityFilters';

interface FinalizeBulkRemovalText {
  completeKey: string;
  completeDefaultValue: string;
  partialFailureKey: string;
  partialFailureDefaultValue: string;
  cancelledKey: string;
  cancelledDefaultValue: string;
  cancelledWithFailuresKey: string;
  cancelledWithFailuresDefaultValue: string;
}

interface FinalizeBulkRemovalNotificationArgs {
  id: string;
  succeeded: number;
  failed: number;
  total: number;
  cancelled: boolean;
  t: TFunction;
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void;
  text: FinalizeBulkRemovalText;
}

interface SharedRemovalHelpers {
  t: TFunction;
  addNotification: NotificationsContextType['addNotification'];
  updateNotification: NotificationsContextType['updateNotification'];
  scheduleRemovalRefresh: (onDataRefresh?: () => void) => void;
  onDataRefresh?: () => void;
}

interface RunTrackedGameRemovalArgs extends SharedRemovalHelpers {
  game: GameCacheInfo;
}

interface RunTrackedServiceRemovalArgs extends SharedRemovalHelpers {
  service: ServiceCacheInfo;
}

interface UseCompletedRemovalPruningArgs {
  notifications: UnifiedNotification[];
  setGames: Dispatch<SetStateAction<GameCacheInfo[]>>;
  setServices: Dispatch<SetStateAction<ServiceCacheInfo[]>>;
  partialRemovalTargetRef?: MutableRefObject<CacheRemovalTarget | null>;
}

export function useScheduledRemovalRefresh(
  delayMs = FULL_REMOVAL_REFRESH_DELAY_MS
): (onDataRefresh?: () => void) => void {
  const schedule = useTimeoutCallback(delayMs);

  return useCallback(
    (onDataRefresh?: () => void): void => {
      if (!onDataRefresh) {
        return;
      }

      schedule(onDataRefresh);
    },
    [schedule]
  );
}

export function useCompletedRemovalPruning({
  notifications,
  setGames,
  setServices,
  partialRemovalTargetRef
}: UseCompletedRemovalPruningArgs): void {
  useEffect(() => {
    setGames((prev) => pruneGamesByCompletedRemovalNotifications(prev, notifications));
    setServices((prev) => pruneServicesByCompletedRemovalNotifications(prev, notifications));

    if (!partialRemovalTargetRef) {
      return;
    }

    const evictionComplete = notifications.some(
      (notification) =>
        notification.type === 'eviction_removal' && notification.status === 'completed'
    );

    if (evictionComplete && partialRemovalTargetRef.current) {
      const removalTarget = partialRemovalTargetRef.current;
      setGames((prev) => pruneGamesByRemovalTarget(prev, removalTarget));
      setServices((prev) => pruneServicesByRemovalTarget(prev, removalTarget));
      partialRemovalTargetRef.current = null;
    }
  }, [notifications, partialRemovalTargetRef, setGames, setServices]);
}

export async function runTrackedGameRemoval({
  game,
  t,
  addNotification,
  updateNotification,
  scheduleRemovalRefresh,
  onDataRefresh
}: RunTrackedGameRemovalArgs): Promise<void> {
  const gameAppId = game.game_app_id;
  const gameName = game.game_name;
  const isEpic = game.service === 'epicgames';
  const epicAppId = game.epic_app_id;

  addNotification({
    type: 'game_removal',
    status: 'running',
    message: t('management.gameDetection.removingGame', { name: gameName }),
    details: isEpic ? { epicAppId, gameName } : { gameAppId, gameName }
  });

  try {
    const response = isEpic
      ? await ApiService.removeEpicGameFromCache(gameName)
      : await ApiService.removeGameFromCache(gameAppId);

    updateNotification(NOTIFICATION_IDS.GAME_REMOVAL, {
      details: {
        operationId: response.operationId,
        gameName,
        ...(isEpic ? (epicAppId ? { epicAppId } : {}) : { gameAppId })
      }
    });

    scheduleRemovalRefresh(onDataRefresh);
  } catch (err: unknown) {
    const errorMsg =
      (err instanceof Error ? err.message : String(err)) ||
      t('management.gameDetection.failedToRemoveGame');

    updateNotification(NOTIFICATION_IDS.GAME_REMOVAL, {
      status: 'failed',
      error: errorMsg
    });

    console.error('Game removal error:', err);
  }
}

export async function runTrackedServiceRemoval({
  service,
  t,
  addNotification,
  updateNotification,
  scheduleRemovalRefresh,
  onDataRefresh
}: RunTrackedServiceRemovalArgs): Promise<void> {
  const serviceName = service.service_name;

  addNotification({
    type: 'service_removal',
    status: 'running',
    message: t('management.gameDetection.removingService', { name: serviceName }),
    details: { service: serviceName }
  });

  try {
    await ApiService.removeServiceFromCache(serviceName);
    scheduleRemovalRefresh(onDataRefresh);
  } catch (err: unknown) {
    const errorMsg =
      (err instanceof Error ? err.message : String(err)) ||
      t('management.gameDetection.failedToRemoveService');

    updateNotification(NOTIFICATION_IDS.SERVICE_REMOVAL, {
      status: 'failed',
      error: errorMsg
    });

    console.error('Service removal error:', err);
  }
}

export const finalizeBulkRemovalNotification = ({
  id,
  succeeded,
  failed,
  total,
  cancelled,
  t,
  updateNotification,
  text
}: FinalizeBulkRemovalNotificationArgs): void => {
  if (cancelled) {
    updateNotification(id, {
      status: 'completed',
      message:
        failed > 0
          ? t(text.cancelledWithFailuresKey, {
              count: succeeded,
              failed,
              total,
              defaultValue: text.cancelledWithFailuresDefaultValue
            })
          : t(text.cancelledKey, {
              count: succeeded,
              total,
              defaultValue: text.cancelledDefaultValue
            }),
      details: { cancelled: true, cancelling: false }
    });
    return;
  }

  if (failed > 0) {
    updateNotification(id, {
      status: 'failed',
      progress: 100,
      message: t(text.partialFailureKey, {
        count: succeeded,
        failed,
        total,
        defaultValue: text.partialFailureDefaultValue
      })
    });
    return;
  }

  updateNotification(id, {
    status: 'completed',
    progress: 100,
    message: t(text.completeKey, {
      count: succeeded,
      total,
      defaultValue: text.completeDefaultValue
    })
  });
};
