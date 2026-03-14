import { createContext } from 'react';

export interface DockerSocketContextType {
  isDockerAvailable: boolean;
  isLoading: boolean;
  refreshDockerStatus: () => Promise<void>;
}

export const DockerSocketContext = createContext<DockerSocketContextType | undefined>(undefined);
