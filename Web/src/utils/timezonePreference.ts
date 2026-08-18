/**
 * Global timezone preference state
 * This allows getEffectiveTimezone to access the preference without circular dependencies
 */

import { createGlobalPreference } from './globalPreference';
import { getPendingValue } from './pendingPreferences';

const timezonePreference = createGlobalPreference(false);

export const setGlobalTimezonePreference = timezonePreference.set;

/**
 * The clock the reader is on, which is not always the one the server has confirmed. The stored
 * value only moves once a save echoes back, so the click is read from the pending entry first.
 * The same `pending ?? stored` the context applies at TimezoneContext.tsx:33, over the same
 * entry, which is what keeps the two from naming different clocks. [60]
 */
export const getGlobalTimezonePreference = (): boolean =>
  getPendingValue<boolean>('useLocalTimezone') ?? timezonePreference.get();
