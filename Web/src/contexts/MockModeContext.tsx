import React, { createContext, useContext, useState, type ReactNode } from 'react';

interface MockModeContextType {
  mockMode: boolean;
  setMockMode: (mode: boolean) => void;
}

const MockModeContext = createContext<MockModeContextType | undefined>(undefined);

export const useMockMode = () => {
  const context = useContext(MockModeContext);
  if (!context) {
    throw new Error('useMockMode must be used within MockModeProvider');
  }
  return context;
};

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
