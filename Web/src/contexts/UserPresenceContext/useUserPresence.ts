import { useContext } from 'react';
import { UserPresenceContext, type UserPresence } from './context';

/**
 * Read this tab's live presence (drives the current session's own status dot). Must be used within a
 * UserPresenceProvider. Consumers re-render when the tab flips between active and idle.
 */
export function useUserPresence(): UserPresence {
  const ctx = useContext(UserPresenceContext);
  if (!ctx) {
    throw new Error('useUserPresence must be used within a UserPresenceProvider');
  }
  return ctx;
}
