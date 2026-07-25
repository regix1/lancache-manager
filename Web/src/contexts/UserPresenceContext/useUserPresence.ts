import { createContextHook } from '../createContextHook';
import { UserPresenceContext } from './context';

/**
 * Read this tab's live presence (drives the current session's own status dot). Must be used within a
 * UserPresenceProvider. Consumers re-render when the tab flips between active and idle.
 */
export const useUserPresence = createContextHook(UserPresenceContext, 'useUserPresence');
