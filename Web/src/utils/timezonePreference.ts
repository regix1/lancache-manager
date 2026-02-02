/**
 * Global timezone preference state
 * This allows formatDateTime to access the preference without circular dependencies
 */

import { createGlobalPreference } from './globalPreference';

const timezonePreference = createGlobalPreference(false);

export const setGlobalTimezonePreference = timezonePreference.set;
export const getGlobalTimezonePreference = timezonePreference.get;

