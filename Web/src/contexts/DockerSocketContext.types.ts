import { createContext } from 'react';

interface DockerSocketContextType {
  isDockerAvailable: boolean;
  isLoading: boolean;
  /** The sentence for the last failed probe, `null` when the last probe answered. */
  error: string | null;
  refreshDockerStatus: () => Promise<void>;
}

export const DockerSocketContext = createContext<DockerSocketContextType | undefined>(undefined);
