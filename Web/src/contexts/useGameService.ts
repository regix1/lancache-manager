import { useContext } from 'react';
import { GameServiceContext, type GameServiceContextType } from './GameServiceContext.types';

export const useGameService = (): GameServiceContextType => {
  const context = useContext(GameServiceContext);
  if (!context) {
    throw new Error('useGameService must be used within GameServiceProvider');
  }
  return context;
};
