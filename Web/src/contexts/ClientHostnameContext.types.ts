import { createContext } from 'react';

interface ClientHostnameContextType {
  // Whether the server is looking hostnames up at all (global setting, off by default)
  enabled: boolean;

  // Loading/error states
  loading: boolean;
  error: string | null;

  // Helper functions
  getHostnameForIp: (clientIp: string) => string | null;

  // Operations
  refreshHostnames: () => Promise<void>;
  setEnabled: (enabled: boolean) => Promise<void>;
}

export const ClientHostnameContext = createContext<ClientHostnameContextType | undefined>(
  undefined
);
