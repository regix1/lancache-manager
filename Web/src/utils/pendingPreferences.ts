/**
 * Centralized utility for handling optimistic preference updates with stale protection.
 *
 * When a user changes a preference, we hold the value they picked during a cooldown period. A
 * SignalR update that arrives while a value is pending is not wrong, only older than the click
 * still in flight, so the pending value wins until the cooldown ends.
 *
 * Usage:
 *   1. Call setPendingTimezone() when user changes a preference (before API call)
 *   2. Use preferPendingTimezone() when processing SignalR updates
 *   3. Use getPendingValue() with useSyncExternalStore for immediate UI updates
 */

import type { TimeSettingValue } from '@contexts/TimezoneContext.types';
import type { ClockPreferences } from '@/types/userPreferences';

type PreferenceValue = boolean | string | number | null;

interface PendingEntry {
  value: PreferenceValue;
  setTime: number;
}

const COOLDOWN_MS = 2000;
const pending = new Map<string, PendingEntry>();
const listeners = new Set<() => void>();

const notify = () => listeners.forEach((fn) => fn());

// ============================================================================
// Core API
// ============================================================================

/**
 * Set a pending preference value. Call when user makes a change.
 */
const setPendingPreference = (key: string, value: PreferenceValue): void => {
  pending.set(key, { value, setTime: Date.now() });
  notify();
};

/**
 * Get the pending value for a preference, or null if none/expired.
 */
export const getPendingValue = <T extends PreferenceValue>(key: string): T | null => {
  const entry = pending.get(key);
  if (!entry) return null;
  if (Date.now() - entry.setTime >= COOLDOWN_MS) {
    pending.delete(key);
    return null;
  }
  return entry.value as T;
};

/**
 * Pick between a pending value and an incoming one. Use when processing SignalR updates.
 * Returns the pending value while one is held, otherwise returns incoming.
 */
const preferPendingValue = <T extends PreferenceValue>(key: string, incoming: T): T => {
  const pendingVal = getPendingValue<T>(key);
  if (pendingVal !== null && incoming !== pendingVal) {
    return pendingVal;
  }
  return incoming;
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
 * nothing about which clock is actually being read while it is on. [17]
 */
export const timeSettingFromClock = (clock: ClockPreferences): TimeSettingValue => {
  if (clock.useUtcTimezone) return 'utc';
  if (clock.useLocalTimezone) return clock.use24HourFormat ? 'local-24h' : 'local-12h';
  return clock.use24HourFormat ? 'server-24h' : 'server-12h';
};

const clockFlags = (value: TimeSettingValue): readonly [string, boolean][] => {
  const clock = clockFromTimeSetting(value);
  return [
    ['useUtcTimezone', clock.useUtcTimezone],
    ['useLocalTimezone', clock.useLocalTimezone],
    ['use24HourFormat', clock.use24HourFormat]
  ];
};

/**
 * Set pending timezone values from a combined time setting. Call when the user picks one.
 */
export const setPendingTimezone = (value: TimeSettingValue): void => {
  clockFlags(value).forEach(([key, flag]) => setPendingPreference(key, flag));
};

/**
 * Drop the optimistic values a failed save had put up, so the control stops showing a choice the
 * server never stored rather than waiting out the cooldown.
 *
 * A save fails while the click after it is already on the wire, and the three flags are only a clock
 * together, so this takes back all three or none. If any entry no longer holds this setting's flag a
 * newer click owns the set, and clearing it would snap the control back to a choice nobody is waiting
 * on while the newer save is still succeeding. [61]
 */
export const dropPendingTimezone = (value: TimeSettingValue): void => {
  const flags = clockFlags(value);
  if (flags.some(([key, flag]) => pending.get(key)?.value !== flag)) {
    return;
  }

  let dropped = false;
  flags.forEach(([key]) => {
    dropped = pending.delete(key) || dropped;
  });
  if (dropped) notify();
};

/**
 * Settle the three clock flags against any pending click, for SignalR updates.
 *
 * All three are settled together because they are written together. Each per-key save echoes back
 * a whole preferences object built from what its own request read, so an echo that raced the UTC
 * write still carries the old value for it; holding only two of the three lets that older value
 * straight through. [2]
 */
export const preferPendingTimezone = (
  incomingUseLocal: boolean,
  incomingUseUtc: boolean,
  incomingUse24Hour: boolean
): { useLocal: boolean; useUtc: boolean; use24Hour: boolean } => ({
  useLocal: preferPendingValue('useLocalTimezone', incomingUseLocal),
  useUtc: preferPendingValue('useUtcTimezone', incomingUseUtc),
  use24Hour: preferPendingValue('use24HourFormat', incomingUse24Hour)
});
