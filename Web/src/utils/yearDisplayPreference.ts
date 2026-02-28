/**
 * Global year display preference state
 * This allows formatDateTime to access the preference without circular dependencies
 */

import { createGlobalPreference } from './globalPreference';

const yearDisplayPreference = createGlobalPreference(false);

export const setGlobalAlwaysShowYearPreference = yearDisplayPreference.set;
export const getGlobalAlwaysShowYearPreference = yearDisplayPreference.get;
