import React, { useMemo } from 'react';
import { useActivityTracker } from '@hooks/useActivityTracker';
import { useClientInfoReporter } from '@hooks/useClientInfoReporter';
import { useAuth } from '@contexts/useAuth';
import { UserPresenceContext, type UserPresence } from './context';

interface UserPresenceProviderProps {
  children: React.ReactNode;
}

/**
 * The single app-wide home for anything that reports THIS browser's own session state to the server.
 * Both reporters below are session-scoped, not page-scoped, so mounting either inside a feature
 * component silently scopes it to that route.
 *
 * useActivityTracker: owns the one and only presence heartbeat. LastSeenAtUtc is refreshed by request
 * traffic carrying X-User-Active, and this heartbeat is the only such request that fires on a timer of
 * its own - every other one depends on the user triggering a fetch. Screens driven by SignalR pushes
 * issue no requests at all while they are merely being watched, so without an app-wide heartbeat a
 * session goes stale and reads as away/inactive while its user is actively working.
 *
 * useClientInfoReporter: reports public IP / locale / screen once per session so the sessions list can
 * show country / city / ISP. The endpoint accepts admin AND guest sessions, but this used to be mounted
 * on the Users page - a route guests are redirected away from - so guest sessions never reported at
 * all and showed blank origins in the very list the feature exists to populate.
 *
 * Both gated on hasSession (true for guest and admin alike): with no session there is nothing to keep
 * alive and nothing to attribute client info to. These come from AuthContext rather than the
 * authService fields, which are plain mutable properties that do not re-render on change.
 */
export const UserPresenceProvider: React.FC<UserPresenceProviderProps> = ({ children }) => {
  const { hasSession, sessionId } = useAuth();
  const { isActive, lastActivityTime } = useActivityTracker(undefined, undefined, hasSession);
  useClientInfoReporter(hasSession, sessionId);

  const value = useMemo<UserPresence>(
    () => ({ isActive, lastActivityTime }),
    [isActive, lastActivityTime]
  );

  return <UserPresenceContext.Provider value={value}>{children}</UserPresenceContext.Provider>;
};
