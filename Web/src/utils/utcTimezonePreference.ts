/**
 * Global "read every time on the UTC clock" preference.
 *
 * A third answer to which clock the app speaks, beside the server's and the browser's, held here
 * for the same reason as the other two: formatTimestamp and getEffectiveTimezone have to reach it
 * without importing a React context. It wins over the local/server choice when it is on, and it is
 * only ever set from the timezone selector and the preferences that selector saves.
 *
 * It is only a default. A caller that builds its settings from the timezone context passes `useUtc`
 * outright, and that always wins - which is what a component must do, because this box holds no
 * subscribers and changing it re-renders nothing.
 */

import { createGlobalPreference } from './globalPreference';

const utcTimezonePreference = createGlobalPreference(false);

export const setGlobalUtcPreference = utcTimezonePreference.set;
export const getGlobalUtcPreference = utcTimezonePreference.get;
