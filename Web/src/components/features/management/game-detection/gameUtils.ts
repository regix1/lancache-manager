import type { GameCacheInfo } from '../../../../types';

/** Generate a unique key for a game, handling Epic games where game_app_id is 0 */
export const getGameUniqueId = (game: GameCacheInfo): string => {
  if (game.service === 'epicgames') {
    return `epic-${game.game_name}`;
  }
  return String(game.game_app_id);
};
