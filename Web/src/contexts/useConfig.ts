import { ConfigContext } from './ConfigContext.types';
import { createContextHook } from './createContextHook';

export const useConfig = createContextHook(ConfigContext, 'useConfig');
