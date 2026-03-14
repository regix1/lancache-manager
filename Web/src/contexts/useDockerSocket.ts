import { useContext } from 'react';
import { DockerSocketContext } from './DockerSocketContext.types';

export const useDockerSocket = () => {
  const context = useContext(DockerSocketContext);
  if (!context) {
    throw new Error('useDockerSocket must be used within DockerSocketProvider');
  }
  return context;
};
