import React, { useState, type ReactNode } from 'react';
import { MockModeContext } from './MockModeContext.types';

interface MockModeProviderProps {
  children: ReactNode;
}

export const MockModeProvider: React.FC<MockModeProviderProps> = ({ children }) => {
  const [mockMode, setMockMode] = useState(false);

  return (
    <MockModeContext.Provider value={{ mockMode, setMockMode }}>
      {children}
    </MockModeContext.Provider>
  );
};
