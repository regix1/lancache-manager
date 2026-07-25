import { EventContext } from './EventContext.types';
import { createContextHook } from './createContextHook';

export const useEvents = createContextHook(EventContext, 'useEvents');
