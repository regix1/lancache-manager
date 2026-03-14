import { useContext } from 'react';
import { RefreshRateContext } from './RefreshRateContext.types';

export const useRefreshRate = () => {
  const context = useContext(RefreshRateContext);
  if (!context) {
    throw new Error('useRefreshRate must be used within RefreshRateProvider');
  }
  return context;
};
