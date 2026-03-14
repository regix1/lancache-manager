import { useContext } from 'react';
import { ClientGroupContext } from './ClientGroupContext.types';

export const useClientGroups = () => {
  const context = useContext(ClientGroupContext);
  if (!context) {
    throw new Error('useClientGroups must be used within a ClientGroupProvider');
  }
  return context;
};
