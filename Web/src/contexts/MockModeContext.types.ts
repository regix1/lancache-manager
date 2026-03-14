import { createContext } from 'react';

interface MockModeContextType {
  mockMode: boolean;
  setMockMode: (mode: boolean) => void;
}

export const MockModeContext = createContext<MockModeContextType | undefined>(undefined);
