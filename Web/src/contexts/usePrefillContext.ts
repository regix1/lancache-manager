import { useContext } from 'react';
import { PrefillContext } from './PrefillContext.types';

export const usePrefillContext = () => {
  const context = useContext(PrefillContext);
  if (!context) {
    throw new Error('usePrefillContext must be used within PrefillProvider');
  }
  return context;
};
