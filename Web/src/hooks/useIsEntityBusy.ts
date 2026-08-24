import { useMemo } from 'react';
import { useNotifications } from '../contexts/notifications/useNotifications';
import type { NotificationType } from '../contexts/notifications/types';
import type { EntityIdentifier } from '@components/features/management/game-detection/gameRemovalEntity';

const DEFAULT_KINDS: NotificationType[] = ['game_removal', 'service_removal', 'eviction_removal'];

export function useIsEntityBusy(
  identifier: EntityIdentifier,
  kinds: NotificationType[] = DEFAULT_KINDS
): boolean {
  const { notifications } = useNotifications();

  const identifierKind = identifier.kind;
  const gameAppId = identifier.kind === 'steamGame' ? identifier.gameAppId : undefined;
  const epicAppId = identifier.kind === 'epicGame' ? identifier.epicAppId : undefined;
  const gameName =
    identifier.kind === 'epicGame' || identifier.kind === 'namedGame'
      ? identifier.gameName
      : undefined;
  const service =
    identifier.kind === 'service' || identifier.kind === 'namedGame'
      ? identifier.service
      : undefined;

  return useMemo(() => {
    return notifications.some((n) => {
      if (!kinds.includes(n.type) || n.status !== 'running') return false;
      if (identifierKind === 'steamGame') return n.details?.gameAppId === gameAppId;
      if (identifierKind === 'epicGame') {
        if (epicAppId !== undefined && n.details?.epicAppId !== undefined) {
          return n.details.epicAppId === epicAppId;
        }
        if (gameName !== undefined) return n.details?.gameName === gameName;
        return false;
      }
      // Named removal notifications carry both `service` and `gameName` in details
      // (see runTrackedGameRemoval). Match on both so a named game does not light up
      // for a same-named service_removal, and two named games never collide.
      if (identifierKind === 'namedGame') {
        return n.details?.service === service && n.details?.gameName === gameName;
      }
      if (identifierKind === 'service') return n.details?.service === service;
      return false;
    });
  }, [notifications, kinds, identifierKind, gameAppId, epicAppId, gameName, service]);
}
