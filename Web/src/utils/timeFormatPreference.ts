/**
 * Global 24-hour format preference state
 * This allows formatDateTime to access the preference without circular dependencies
 */

import { createGlobalPreference } from './globalPreference';

const timeFormatPreference = createGlobalPreference(true); // Default to 24-hour format

export const setGlobal24HourPreference = timeFormatPreference.set;
export const getGlobal24HourPreference = timeFormatPreference.get;
