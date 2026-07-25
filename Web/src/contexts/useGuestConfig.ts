import { GuestConfigContext } from './GuestConfigContext.types';
import { createContextHook } from './createContextHook';

export const useGuestConfig = createContextHook(GuestConfigContext, 'useGuestConfig');
