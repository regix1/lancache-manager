import { createContextHook } from '../createContextHook';
import { SpeedContext } from './SpeedContext.types';

export const useSpeed = createContextHook(SpeedContext, 'useSpeed');
