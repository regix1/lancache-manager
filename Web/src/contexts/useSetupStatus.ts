import { useContext } from 'react';
import { SetupStatusContext } from './SetupStatusContext.types';

export const useSetupStatus = () => {
  const context = useContext(SetupStatusContext);
  if (!context) {
    throw new Error('useSetupStatus must be used within SetupStatusProvider');
  }
  return context;
};
