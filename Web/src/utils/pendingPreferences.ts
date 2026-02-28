/**
 * Centralized utility for handling optimistic preference updates with stale protection.
 *
 * When a user changes a preference, we track the "expected value" during a cooldown period.
 * Any SignalR updates that arrive with stale data are corrected to use the expected value.
 *
 * Usage:
 *   1. Call setPendingPreference() when user changes a preference (before API call)
 *   2. Use getCorrectedValue() when processing SignalR updates
 *   3. Use usePendingValue() hook in React components for immediate UI updates
 */

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
 * Check if a preference has a pending value (within cooldown).
 */
export const hasPendingPreference = (key: string): boolean => {
  const entry = pending.get(key);
  if (!entry) return false;
  if (Date.now() - entry.setTime >= COOLDOWN_MS) {
    pending.delete(key);
    return false;
  }
  return true;
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
 * Get the corrected value for a preference. Use when processing SignalR updates.
 * Returns the pending value if incoming is stale, otherwise returns incoming.
 */
export const getCorrectedValue = <T extends PreferenceValue>(key: string, incoming: T): T => {
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

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

/**
 * Set pending timezone values from a combined time setting.
 */
export const setPendingTimezone = (value: TimeSettingValue | null): void => {
  if (value !== null) {
    setPendingPreference('useLocalTimezone', value.startsWith('local'));
    setPendingPreference('use24HourFormat', value.endsWith('24h'));
  }
};

/**
 * Get corrected timezone values for SignalR updates.
 */
export const getCorrectedTimezone = (
  incomingUseLocal: boolean,
  incomingUse24Hour: boolean
): { useLocal: boolean; use24Hour: boolean } => ({
  useLocal: getCorrectedValue('useLocalTimezone', incomingUseLocal),
  use24Hour: getCorrectedValue('use24HourFormat', incomingUse24Hour)
});
