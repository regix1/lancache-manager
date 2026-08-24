import type { GameCacheInfo } from '../../../../types';

/**
 * Platform identity for cache-game removal.
 *
 * Key formats (do not unify):
 * - UI list keys: hyphen (`epic-{name}`, `{service}-{name}`, `{appId}`) via getGameUniqueId
 * - Conflict scopes: colon (`{service}:{gameName}`) on the backend
 * - Eviction Started context: scope + key (epic key is epic_app_id; named is `{service}:{gameName}`)
 */
export type GameEntityIdentifier =
  | { kind: 'steamGame'; gameAppId: number }
  | { kind: 'epicGame'; epicAppId?: string; gameName?: string }
  // Named (Blizzard/Riot/Xbox) games have no Steam/Epic id; identity is (service, gameName).
  // Every named game shares gameAppId 0, so the steamGame arm would collide them.
  | { kind: 'namedGame'; service: string; gameName: string };

export type EntityIdentifier = GameEntityIdentifier | { kind: 'service'; service: string };

interface GameRemovalIdentity {
  gameAppId?: number | null;
  epicAppId?: string | null;
  gameName?: string;
  service?: string | null;
  operationId?: string;
}

export function classifyGameFromCacheInfo(game: GameCacheInfo): GameEntityIdentifier {
  if (game.service === 'epicgames') {
    return {
      kind: 'epicGame',
      epicAppId: game.epic_app_id,
      gameName: game.game_name
    };
  }

  if (game.game_app_id === 0 && !!game.service && game.service !== 'steam') {
    return {
      kind: 'namedGame',
      service: game.service,
      gameName: game.game_name
    };
  }

  return { kind: 'steamGame', gameAppId: game.game_app_id };
}

/** Identity-only. Never reads operationId — used for Started capture after queue promotion. */
export function matchesGameRemovalIdentity(
  event: GameRemovalIdentity | undefined,
  entity: EntityIdentifier
): boolean {
  if (!event) {
    return false;
  }

  if (entity.kind === 'epicGame') {
    if (entity.epicAppId && event.epicAppId === entity.epicAppId) {
      return true;
    }

    return event.gameName === entity.gameName;
  }

  if (entity.kind === 'namedGame') {
    if (event.gameName !== entity.gameName) {
      return false;
    }

    return event.service == null || event.service === entity.service;
  }

  if (entity.kind === 'steamGame') {
    return event.gameAppId === entity.gameAppId;
  }

  return false;
}

/**
 * Complete-event match: after a running id is captured, prefer that id; before capture
 * (or when the HTTP body was a waiting id we refused to pin), fall back to identity.
 */
export function matchesGameRemovalComplete(
  event: GameRemovalIdentity | undefined,
  entity: EntityIdentifier,
  capturedOpId: string | null
): boolean {
  if (!event) {
    return false;
  }

  if (capturedOpId) {
    return event.operationId === capturedOpId;
  }

  return matchesGameRemovalIdentity(event, entity);
}

export function shouldPinOperationIdFromResponse(response: {
  operationId?: string;
  queued?: boolean;
  alreadyRunning?: boolean;
}): response is { operationId: string; queued?: boolean; alreadyRunning?: boolean } {
  return Boolean(response.operationId) && !response.queued && !response.alreadyRunning;
}
