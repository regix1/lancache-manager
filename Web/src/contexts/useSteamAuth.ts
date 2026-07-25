import { SteamAuthContext } from './SteamAuthContext.types';
import { createContextHook } from './createContextHook';

export const useSteamAuth = createContextHook(SteamAuthContext, 'useSteamAuth');
