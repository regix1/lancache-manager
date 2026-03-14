import { useContext } from 'react';
import { SpeedContext } from './SpeedContext.types';
import type { SpeedContextType } from './types';

export const useSpeed = (): SpeedContextType => {
  const context = useContext(SpeedContext);
  if (!context) {
    throw new Error('useSpeed must be used within SpeedProvider');
  }
  return context;
};
