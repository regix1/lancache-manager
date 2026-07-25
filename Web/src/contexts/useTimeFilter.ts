import { TimeFilterContext } from './TimeFilterContext.types';
import { createContextHook } from './createContextHook';

export const useTimeFilter = createContextHook(TimeFilterContext, 'useTimeFilter');
