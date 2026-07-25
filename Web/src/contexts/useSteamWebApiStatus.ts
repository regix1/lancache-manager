import { SteamWebApiStatusContext } from './SteamWebApiStatusContext.types';
import { createContextHook } from './createContextHook';

export const useSteamWebApiStatus = createContextHook(
  SteamWebApiStatusContext,
  'useSteamWebApiStatus'
);
