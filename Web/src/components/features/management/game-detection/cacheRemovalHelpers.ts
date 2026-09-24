import {
  useCallback,
  useEffect,
  type Dispatch,
  type MutableRefObject,
  type SetStateAction
} from 'react';
import ApiService from '@services/api.service';
import {
  FAILED_TO_REMOVE_GAME_I18N_KEY,
  FULL_PROGRESS_PERCENT
} from '@contexts/notifications/constants';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type {
  EvictionRemovalCompleteEvent,
  GameRemovalCompleteEvent,
  ServiceRemovalCompleteEvent
} from '@contexts/SignalRContext/types';
import type { TFunction } from 'i18next';
import type { NotificationsContextType } from '@contexts/notifications/types';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';
import type { GameCacheInfo, ServiceCacheInfo } from '../../../../types';
import {
  pruneGamesByRemovalTarget,
  pruneServicesByRemovalTarget,
  type CacheRemovalTarget
} from './cacheDetectionData';
import { FULL_REMOVAL_REFRESH_DELAY_MS } from './cacheEntityFilters';
import { classifyGameFromCacheInfo } from './gameRemovalEntity';

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
  updateNotification: NotificationsContextType['updateNotification'];
  text: FinalizeBulkRemovalText;
}

interface SharedRemovalHelpers {
  notifyError: (userMessage: string, error?: unknown) => void;
  t: TFunction;
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
  setGames: Dispatch<SetStateAction<GameCacheInfo[]>>;
  setServices: Dispatch<SetStateAction<ServiceCacheInfo[]>>;
  removalTargetRef?: MutableRefObject<CacheRemovalTarget | null>;
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
  setGames,
  setServices,
  removalTargetRef
}: UseCompletedRemovalPruningArgs): void {
  const { on, off } = useSignalR();

  useEffect(() => {
    // A canceled or failed removal leaves the entity on disk, so only a success prunes it.
    const handleGameRemovalComplete = (event: GameRemovalCompleteEvent) => {
      if (!event.success || event.cancelled === true) return;
      const target: CacheRemovalTarget = {
        gameAppId: event.gameAppId ?? undefined,
        epicAppId: event.epicAppId ?? undefined,
        gameName: event.gameName
      };
      setGames((prev) => pruneGamesByRemovalTarget(prev, target));
    };

    const handleServiceRemovalComplete = (event: ServiceRemovalCompleteEvent) => {
      if (!event.success || event.cancelled === true) return;
      const target: CacheRemovalTarget = { serviceName: event.serviceName };
      setServices((prev) => pruneServicesByRemovalTarget(prev, target));
    };

    const handleEvictionRemovalComplete = (event: EvictionRemovalCompleteEvent) => {
      if (!event.success || event.cancelled === true || !removalTargetRef?.current) return;
      const removalTarget = removalTargetRef.current;
      setGames((prev) => pruneGamesByRemovalTarget(prev, removalTarget));
      setServices((prev) => pruneServicesByRemovalTarget(prev, removalTarget));
      removalTargetRef.current = null;
    };

    on('GameRemovalComplete', handleGameRemovalComplete);
    on('ServiceRemovalComplete', handleServiceRemovalComplete);
    on('EvictionRemovalComplete', handleEvictionRemovalComplete);
    return () => {
      off('GameRemovalComplete', handleGameRemovalComplete);
      off('ServiceRemovalComplete', handleServiceRemovalComplete);
      off('EvictionRemovalComplete', handleEvictionRemovalComplete);
    };
  }, [on, off, removalTargetRef, setGames, setServices]);
}

// The server's run row opens the removal's card. A request the server never accepted has no
// run, so its failure is told here.
export async function runTrackedGameRemoval({
  game,
  notifyError,
  t,
  scheduleRemovalRefresh,
  onDataRefresh
}: RunTrackedGameRemovalArgs): Promise<void> {
  const entity = classifyGameFromCacheInfo(game);

  try {
    await (entity.kind === 'epicGame'
      ? ApiService.removeEpicGameFromCache(game.game_name)
      : entity.kind === 'namedGame'
        ? ApiService.removeNamedGameFromCache(entity.service, entity.gameName)
        : ApiService.removeGameFromCache(entity.gameAppId));

    scheduleRemovalRefresh(onDataRefresh);
  } catch (err: unknown) {
    notifyError(t(FAILED_TO_REMOVE_GAME_I18N_KEY), err);
  }
}

export async function runTrackedServiceRemoval({
  service,
  notifyError,
  t,
  scheduleRemovalRefresh,
  onDataRefresh
}: RunTrackedServiceRemovalArgs): Promise<void> {
  try {
    await ApiService.removeServiceFromCache(service.service_name);

    scheduleRemovalRefresh(onDataRefresh);
  } catch (err: unknown) {
    notifyError(t('management.gameDetection.failedToRemoveService'), err);
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
  // A canceled batch follows the run ending rules. `details` merges only at the top level, so the
  // function form keeps the item ids that hold the batch's kept failures inside this card. An item
  // that failed before the server gave it a run is held by this card alone, so the card turns red
  // and stays until closed rather than leaving gray with that failure.
  if (cancelled) {
    updateNotification(id, (card) => {
      const holdsFailure = card.details?.failedWithoutRun === true;
      return {
        status: holdsFailure ? 'failed' : 'cancelled',
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
        details: { ...card.details, cancelled: !holdsFailure, cancelling: false }
      };
    });
    return;
  }

  if (failed > 0) {
    updateNotification(id, {
      status: 'failed',
      progress: FULL_PROGRESS_PERCENT,
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
    progress: FULL_PROGRESS_PERCENT,
    message: t(text.completeKey, {
      count: succeeded,
      total,
      defaultValue: text.completeDefaultValue
    })
  });
};
