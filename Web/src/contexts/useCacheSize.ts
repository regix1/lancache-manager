import { useContext } from 'react';
import { CacheSizeContext } from './CacheSizeContext.types';

export const useCacheSize = () => {
  const context = useContext(CacheSizeContext);
  if (!context) {
    throw new Error('useCacheSize must be used within CacheSizeProvider');
  }
  return context;
};
