import { MockModeContext } from './MockModeContext.types';
import { createContextHook } from './createContextHook';

export const useMockMode = createContextHook(MockModeContext, 'useMockMode');
