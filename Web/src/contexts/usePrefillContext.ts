import { PrefillContext } from './PrefillContext.types';
import { createContextHook } from './createContextHook';

export const usePrefillContext = createContextHook(PrefillContext, 'usePrefillContext');
