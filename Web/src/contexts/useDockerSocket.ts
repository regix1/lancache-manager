import { DockerSocketContext } from './DockerSocketContext.types';
import { createContextHook } from './createContextHook';

export const useDockerSocket = createContextHook(DockerSocketContext, 'useDockerSocket');
