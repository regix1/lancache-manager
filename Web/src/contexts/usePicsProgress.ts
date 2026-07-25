import { PicsProgressContext } from './PicsProgressContext.types';
import { createContextHook } from './createContextHook';

export const usePicsProgress = createContextHook(PicsProgressContext, 'usePicsProgress');
