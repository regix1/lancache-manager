import { useContext } from 'react';
import { MockModeContext } from './MockModeContext.types';

export const useMockMode = () => {
  const context = useContext(MockModeContext);
  if (!context) {
    throw new Error('useMockMode must be used within MockModeProvider');
  }
  return context;
};
