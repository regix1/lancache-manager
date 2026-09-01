/**
 * Centralized utility for handling optimistic preference updates with stale protection.
 *
 * When a user changes a preference, we hold the value they picked until the save behind it answers.
 * A SignalR update that arrives while a value is pending is not wrong, only older than the click
 * still in flight, so the pending value wins until the save behind that click answers.
 *
 * Usage:
 *   1. Call setPendingTimezone() when user changes a preference (before API call)
 *   2. Use preferPendingTimezone() when processing SignalR updates
 *   3. Use getPendingValue() with useSyncExternalStore for immediate UI updates
 */

import type { TimeSettingValue } from '@contexts/TimezoneContext.types';
import { CLOCK_KEYS, type ClockPreferences } from '../types/userPreferences.ts';

type PreferenceValue = boolean | string | number | null;

interface PendingEntry {
  value: PreferenceValue;
  /** Which pick wrote it. The three flags of one pick all carry the same number. */
  click: number;
}

const pending = new Map<string, PendingEntry>();
const listeners = new Set<() => void>();
let clicks = 0;

/**
 * The pick whose save has answered yes, and the pick whose values an update has carried to the
 * readers. A pick is released once both have happened to it, in whichever order they arrive:
 * released before its save answers it stops shielding a click still on the wire, and released
 * before the readers hold its values every one of them drops back to the clock it replaced. Both
 * name a pick by its number, never by what it holds.
 */
let confirmedClick: number | null = null;
let echoedClick: number | null = null;

const notify = () => listeners.forEach((fn) => fn());

// ============================================================================
// Core API
// ============================================================================

/**
 * Get the pending value for a preference, or null if none is held. A plain map read, safe in a
 * render.
 */
export const getPendingValue = <T extends PreferenceValue>(key: string): T | null => {
  const entry = pending.get(key);
  return entry ? (entry.value as T) : null;
};

/**
 * Pick between a pending value and an incoming one. Use when processing SignalR updates.
 * Returns the pending value while one is held, otherwise returns incoming.
 */
export const preferPendingValue = <T extends PreferenceValue>(key: string, incoming: T): T => {
  const pendingVal = getPendingValue<T>(key);
  if (pendingVal !== null && incoming !== pendingVal) {
    return pendingVal;
  }
  return incoming;
};

/**
 * Hold the value a toggle was just switched to until its own save answers.
 *
 * A single preference stands alone, so it is held and released by its key rather than by the pick
 * number the clock needs: the clock's three columns are one choice and have to be taken back
 * together, while nothing else here writes more than the one key.
 */
export const setPendingPreference = (key: string, value: PreferenceValue): void => {
  pending.set(key, { value, click: (clicks += 1) });
  notify();
};

/** Release a value held by setPendingPreference, whatever its save answered. */
export const dropPendingPreference = (key: string): void => {
  if (pending.delete(key)) notify();
};

// ============================================================================
// React Integration
// ============================================================================

/**
 * Subscribe to pending preference changes. For use with useSyncExternalStore.
 */
export const subscribe = (listener: () => void): (() => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};

// ============================================================================
// Convenience: Timezone Helper
// ============================================================================

/**
 * The three clock flags a time setting stands for. UTC carries no 12/24 half of its own, so it puts
 * the 24-hour face on: there is no 12-hour UTC to go back to, and leaving the old face in place would
 * show "4:57 AM UTC" beside a control that no longer offers the choice.
 */
export const clockFromTimeSetting = (value: TimeSettingValue): ClockPreferences => {
  const isUtc = value === 'utc';
  return {
    useUtcTimezone: isUtc,
    useLocalTimezone: value.startsWith('local'),
    use24HourFormat: isUtc || value.endsWith('24h')
  };
};

/**
 * Which time setting three stored flags name. The exact inverse of clockFromTimeSetting, and the
 * only place that answers the question: a second copy is how a control ends up naming one clock
 * while the flags behind it say another. UTC answers first, because the local/server pair says
 * nothing about which clock is actually being read while it is on.
 */
export const timeSettingFromClock = (clock: ClockPreferences): TimeSettingValue => {
  if (clock.useUtcTimezone) return 'utc';
  if (clock.useLocalTimezone) return clock.use24HourFormat ? 'local-24h' : 'local-12h';
  return clock.use24HourFormat ? 'server-24h' : 'server-12h';
};

/**
 * Set pending timezone values from a combined time setting. Call when the user picks one. Hands
 * back the number that identifies the pick, for dropPendingTimezone.
 *
 * Held until its own save answers, never on a timer: a timer either ends a save still running, which
 * puts the reader back on a clock the server is about to disagree with, or outlives one that
 * already answered.
 */
export const setPendingTimezone = (value: TimeSettingValue): number => {
  const clock = clockFromTimeSetting(value);
  const click = (clicks += 1);
  CLOCK_KEYS.forEach((key) => pending.set(key, { value: clock[key], click }));
  notify();
  return click;
};

/**
 * Take back the optimistic values a pick put up, when its save failed or the server confirmed it.
 *
 * The three flags are only a clock together, so this takes all three or none, in one notification.
 * A pick owns its entries by its own number rather than by the values it wrote, because two picks
 * of the same setting write the same values: comparing those lets a failed save take back the pick
 * that came after it.
 */
export const dropPendingTimezone = (click: number): void => {
  let dropped = false;
  pending.forEach((entry, key) => {
    if (entry.click !== click) return;
    pending.delete(key);
    dropped = true;
  });
  if (dropped) notify();
};

/**
 * The save behind a pick answered yes, so the server is holding what the pick put up. Release the
 * pick if the readers already have those values, otherwise the next update to arrive releases it:
 * dropping it here with the readers still on the old clock is what a wall-clock timer used to do.
 */
export const confirmPendingTimezone = (click: number): void => {
  if (echoedClick === click) {
    dropPendingTimezone(click);
    return;
  }
  confirmedClick = click;
};

/**
 * Settle the three clock flags against any pending click, for SignalR updates. All three are
 * settled together because they are written together.
 */
export const preferPendingTimezone = (
  incomingUseLocal: boolean,
  incomingUseUtc: boolean,
  incomingUse24Hour: boolean
): { useLocal: boolean; useUtc: boolean; use24Hour: boolean } => {
  const held = pending.get('useUtcTimezone');

  // Confirmed and now overtaken: whatever this update carries is the newest word there is, and a
  // pick kept past it would outrank every later change for as long as the tab is open.
  if (held && held.click === confirmedClick) {
    dropPendingTimezone(held.click);
    return { useLocal: incomingUseLocal, useUtc: incomingUseUtc, use24Hour: incomingUse24Hour };
  }

  // Whether the readers now hold this pick, which decides whether its own save may release it. An
  // update carrying two of the three raced the write of the flag it disagrees with.
  if (held) {
    echoedClick =
      held.value === incomingUseUtc &&
      pending.get('useLocalTimezone')?.value === incomingUseLocal &&
      pending.get('use24HourFormat')?.value === incomingUse24Hour
        ? held.click
        : null;
  }

  return {
    useLocal: preferPendingValue('useLocalTimezone', incomingUseLocal),
    useUtc: preferPendingValue('useUtcTimezone', incomingUseUtc),
    use24Hour: preferPendingValue('use24HourFormat', incomingUse24Hour)
  };
};
