/**
 * Global "read every time on the UTC clock" preference.
 *
 * A third answer to which clock the app speaks, beside the server's and the browser's, held here
 * for the same reason as the other two: formatTimestamp and getEffectiveTimezone have to reach it
 * without importing a React context. It wins over the local/server choice when it is on, and it is
 * only ever set from the timezone selector and the preferences that selector saves.
 *
 * It is only a default. A caller that passes `useUtc` outright still wins, and the reader below
 * answers with the pending click ahead of the stored value.
 */

import { createGlobalPreference } from './globalPreference';
import { getPendingValue } from './pendingPreferences';

const utcTimezonePreference = createGlobalPreference(false);

export const setGlobalUtcPreference = utcTimezonePreference.set;

/** The UTC half of the reader's clock, read the same way as getGlobalTimezonePreference. */
export const getGlobalUtcPreference = (): boolean =>
  getPendingValue<boolean>('useUtcTimezone') ?? utcTimezonePreference.get();
