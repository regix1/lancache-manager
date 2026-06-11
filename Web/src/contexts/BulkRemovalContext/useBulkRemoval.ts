import { useContext } from 'react';
import { BulkRemovalContext } from './BulkRemovalContext.types';

export const useBulkRemoval = () => {
  const context = useContext(BulkRemovalContext);
  if (!context) {
    throw new Error('useBulkRemoval must be used within BulkRemovalProvider');
  }
  return context;
};
