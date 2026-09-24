import { useMemo } from 'react';
import { useNotifications } from '../contexts/notifications/useNotifications';
import type { NotificationType } from '../contexts/notifications/types';
import type { EntityIdentifier } from '@components/features/management/game-detection/gameRemovalEntity';

const DEFAULT_KINDS: NotificationType[] = ['game_removal', 'service_removal', 'eviction_removal'];

export function useIsEntityBusy(
  identifier: EntityIdentifier,
  kinds: NotificationType[] = DEFAULT_KINDS
): boolean {
  const { runs } = useNotifications();

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
    return runs.some((n) => {
      // A cancelling removal still owns its entity until it unwinds. A waiting one carries no
      // game or service yet (its record holds only the queue's park state), so it never matches.
      if (!kinds.includes(n.type) || (n.status !== 'running' && n.status !== 'cancelling')) {
        return false;
      }
      if (identifierKind === 'steamGame') return n.details?.gameAppId === gameAppId;
      if (identifierKind === 'epicGame') {
        if (epicAppId !== undefined && n.details?.epicAppId !== undefined) {
          return n.details.epicAppId === epicAppId;
        }
        // By name only an Epic run counts: an Xbox or Battle.net game can carry the same name.
        if (gameName !== undefined)
          return n.details?.service === 'epicgames' && n.details.gameName === gameName;
        return false;
      }
      // A named game's removal names its service; a Steam removal names none, so neither a
      // same-named game on another service nor a same-named Steam game matches.
      if (identifierKind === 'namedGame') {
        return n.details?.gameName === gameName && n.details?.service === service;
      }
      // A service card is busy only for a run over the whole service, one that names no game.
      return (
        n.details?.service === service && !n.details?.gameName && n.details?.epicAppId === undefined
      );
    });
  }, [runs, kinds, identifierKind, gameAppId, epicAppId, gameName, service]);
}
