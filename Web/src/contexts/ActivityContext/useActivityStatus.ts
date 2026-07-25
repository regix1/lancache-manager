import { createContextHook } from '../createContextHook';
import { ActivityContext } from './context';

/**
 * Access the unified activity/presence state (drives every green status dot). Must be used within an
 * ActivityProvider. Returns a stable lookup object; consumers re-render when the underlying snapshot
 * changes.
 */
export const useActivityStatus = createContextHook(ActivityContext, 'useActivityStatus');
