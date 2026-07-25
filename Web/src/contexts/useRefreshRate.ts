import { RefreshRateContext } from './RefreshRateContext.types';
import { createContextHook } from './createContextHook';

export const useRefreshRate = createContextHook(RefreshRateContext, 'useRefreshRate');
