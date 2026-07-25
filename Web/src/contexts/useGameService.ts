import { GameServiceContext } from './GameServiceContext.types';
import { createContextHook } from './createContextHook';

export const useGameService = createContextHook(GameServiceContext, 'useGameService');
