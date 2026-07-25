import { CacheSizeContext } from './CacheSizeContext.types';
import { createContextHook } from './createContextHook';

export const useCacheSize = createContextHook(CacheSizeContext, 'useCacheSize');
