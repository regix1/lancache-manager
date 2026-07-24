import { useContext } from 'react';
import { ActivityContext, type ActivityLookup } from './context';

/**
 * Access the unified activity/presence state (drives every green status dot). Must be used within an
 * ActivityProvider. Returns a stable lookup object; consumers re-render when the underlying snapshot
 * changes.
 */
export function useActivityStatus(): ActivityLookup {
  const ctx = useContext(ActivityContext);
  if (!ctx) {
    throw new Error('useActivityStatus must be used within an ActivityProvider');
  }
  return ctx;
}
