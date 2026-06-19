import type { GameCacheInfo } from '../../../../types';

/**
 * Generate a unique key for a game.
 * - Steam games: keyed by game_app_id (always > 0 for Steam).
 * - Named games (Blizzard/Riot/Epic): game_app_id === 0, keyed by service + game_name
 *   to prevent React duplicate-key collapse when multiple games share appId 0.
 */
export const getGameUniqueId = (game: GameCacheInfo): string => {
  if (game.service === 'epicgames') {
    return `epic-${game.game_name}`;
  }
  if (game.game_app_id === 0 && game.service && game.service !== 'steam') {
    return `${game.service}-${game.game_name}`;
  }
  return String(game.game_app_id);
};
