import { useMemo } from 'react';
import { useNotifications } from '../contexts/notifications/useNotifications';
import type { NotificationType } from '../contexts/notifications/types';

type EntityIdentifier =
  | { kind: 'steamGame'; gameAppId: number }
  | { kind: 'epicGame'; gameName: string }
  | { kind: 'service'; service: string };

const DEFAULT_KINDS: NotificationType[] = ['game_removal', 'service_removal', 'eviction_removal'];

export function useIsEntityBusy(
  identifier: EntityIdentifier,
  kinds: NotificationType[] = DEFAULT_KINDS
): boolean {
  const { notifications } = useNotifications();

  const identifierKind = identifier.kind;
  const gameAppId = identifier.kind === 'steamGame' ? identifier.gameAppId : undefined;
  const gameName = identifier.kind === 'epicGame' ? identifier.gameName : undefined;
  const service = identifier.kind === 'service' ? identifier.service : undefined;

  return useMemo(() => {
    return notifications.some((n) => {
      if (!kinds.includes(n.type) || n.status !== 'running') return false;
      if (identifierKind === 'steamGame') return n.details?.gameAppId === gameAppId;
      if (identifierKind === 'epicGame') return n.details?.gameName === gameName;
      if (identifierKind === 'service') return n.details?.service === service;
      return false;
    });
  }, [notifications, kinds, identifierKind, gameAppId, gameName, service]);
}
