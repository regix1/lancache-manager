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
