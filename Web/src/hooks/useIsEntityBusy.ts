import { useMemo } from 'react';
import { useNotifications } from '../contexts/notifications/useNotifications';
import type { NotificationType } from '../contexts/notifications/types';

type EntityIdentifier =
  | { kind: 'steamGame'; gameAppId: number }
  | { kind: 'epicGame'; epicAppId?: string; gameName?: string }
  // Named (Blizzard/Riot) games have no Steam/Epic id; identity is (service, gameName).
  // Every named game shares gameAppId 0, so the steamGame arm would collide them.
  | { kind: 'namedGame'; service: string; gameName: string }
  | { kind: 'service'; service: string };

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
