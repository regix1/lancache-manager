import { useContext } from 'react';
import { PicsProgressContext } from './PicsProgressContext.types';

export const usePicsProgress = () => {
  const context = useContext(PicsProgressContext);
  if (!context) {
    throw new Error('usePicsProgress must be used within PicsProgressProvider');
  }
  return context;
};
