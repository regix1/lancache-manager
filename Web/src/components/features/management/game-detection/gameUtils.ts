import type { GameCacheInfo } from '../../../../types';
import { classifyGameFromCacheInfo } from './gameRemovalEntity';

/**
 * Generate a unique key for a game.
 * - Steam games: keyed by game_app_id (always > 0 for Steam).
 * - Epic games: keyed by name (`epic-{name}`).
 * - Named games (Blizzard/Riot/Xbox): game_app_id === 0, keyed by service + game_name
 *   to prevent React duplicate-key collapse when multiple games share appId 0.
 */
export const getGameUniqueId = (game: GameCacheInfo): string => {
  const entity = classifyGameFromCacheInfo(game);
  if (entity.kind === 'epicGame') {
    return `epic-${game.game_name}`;
  }
  if (entity.kind === 'namedGame') {
    return `${entity.service}-${entity.gameName}`;
  }
  return String(entity.gameAppId);
};
