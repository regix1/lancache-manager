import { createContext } from 'react';
import type { ClientHostnamesReason } from '@services/api.service';

interface ClientHostnameContextType {
  // Whether the server is looking hostnames up at all (global setting, off by default)
  enabled: boolean;

  // Why the map is empty or partial when a lookup did not turn up a name for every address.
  // 'none' when there is nothing to explain (every address got a name, or the toggle is off).
  reason: ClientHostnamesReason;

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
