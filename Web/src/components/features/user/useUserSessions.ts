import {
  type ComponentProps,
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState
} from 'react';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useMockMode } from '@contexts/useMockMode';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useActivityStatus } from '@contexts/ActivityContext/useActivityStatus';
import { useUserPresence } from '@contexts/UserPresenceContext/useUserPresence';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useManagerLoading } from '@hooks/useManagerLoading';
import { getErrorMessage } from '@utils/error';
import { parseUtcDate } from '@utils/timezone';
import { prefillServiceConfig } from '@components/features/prefill/hooks/prefillServiceConfig';
import type StatusDot from '@components/common/StatusDot';
import type { Session } from './types';

// How old lastSeenAt may be before a session stops reading as "active". This MUST stay comfortably
// larger than the rate at which the server actually refreshes lastSeenAt, or a session whose user is
// working right now will still age past it between two consecutive writes.
//
// The server throttles the write to once per 60s (SessionService.UpdateLastSeenAsync) and the browser
// heartbeat runs on a 30s timer (useActivityTracker), so a write lands on the first heartbeat at or
// after the throttle expires - up to 60 + 30 seconds after the previous one, and the two timers drift
// apart rather than staying phase-locked, since goActive() also fires heartbeats off-cadence. Add the
// browser-vs-server clock skew that "now - lastSeenAt" is computed across and a 60s window is
// guaranteed to lapse mid-cycle, which read as the presence dot flickering active/away every minute.
// Ambient API traffic used to hide this by refreshing lastSeenAt constantly; presence is now
// deliberately driven by the heartbeat alone, so the window has to cover the heartbeat's own period.
const ACTIVE_MAX_AGE_SECONDS = 60 + 30 + 30;
// Beyond this the session reads as fully inactive rather than merely away.
const AWAY_MAX_AGE_SECONDS = 600;

// Events after which the whole list is read again. Disabling or deleting an account revokes its
// sessions on the server without a session event, so AccountsChanged is one of them.
const RELOAD_EVENTS = [
  'UserSessionRevoked',
  'UserSessionsCleared',
  'UserSessionCreated',
  'GuestDurationUpdated',
  'UserPreferencesReset',
  'GuestPrefillConfigChanged',
  'EpicGuestPrefillConfigChanged',
  'BattleNetGuestPrefillConfigChanged',
  'RiotGuestPrefillConfigChanged',
  'XboxGuestPrefillConfigChanged',
  'GuestRefreshRateUpdated',
  'AccountsChanged'
] as const;

type SessionStatus = NonNullable<ComponentProps<typeof StatusDot>['state']>;

interface SessionsResponse {
  sessions: Session[];
  pagination: { totalPages: number; totalCount: number; page: number };
  historySessions: Session[];
}

interface UserSessionsState {
  /** Active sessions, every server page. */
  sessions: Session[];
  /** Ended sessions. */
  historySessions: Session[];
  loading: boolean;
  loadError: string | null;
  /** True once one load finished without error; a later failure never clears it. */
  loaded: boolean;
  /** showLoading shows the loading state while nothing is loaded yet (Retry); change events pass false. */
  reload: (showLoading?: boolean) => Promise<void>;
  getSessionStatus: (session: Session) => SessionStatus;
}

/** The Users page's one session loader: rows, live updates and presence. */
export const useUserSessions = (): UserSessionsState => {
  const { accountId, sessionId, authenticationEnabled } = useAuth();
  const { mockMode } = useMockMode();
  const { on, off, isConnected } = useSignalR();
  // Session presence flows through the unified activity registry; the last-seen aging below still
  // decides active vs away, so away/inactive is never collapsed into a boolean.
  const activity = useActivityStatus();
  // Read the app-wide presence tracker rather than mounting one here: a tracker owned by this page
  // would stop heartbeating the moment the user navigated away (see UserPresenceProvider).
  const { isActive: isLocallyActive } = useUserPresence();
  const sessionIdentity = mockMode
    ? 'mock'
    : authenticationEnabled
      ? `account:${accountId ?? 'none'}:${sessionId ?? 'none'}`
      : `shared:${sessionId ?? 'none'}`;

  const [sessions, setSessions] = useState<Session[]>([]);
  const [historySessions, setHistorySessions] = useState<Session[]>([]);
  const [loadError, setLoadError] = useState<string | null>(null);
  // Starts loading so the first paint shows the skeleton, never the empty state.
  const {
    isLoading: loading,
    hasInitiallyLoaded: loaded,
    setLoading,
    markLoaded,
    markFailed
  } = useManagerLoading(true);
  // Periodic tick so getSessionStatus() recomputes as lastSeenAt ages.
  // Without this, the status "sticks" between render-triggering events and
  // flips abruptly when some unrelated re-render happens.
  const [, setStatusTick] = useState<number>(0);
  const sessionIdentityRef = useRef(sessionIdentity);
  const loadRequestRef = useRef(0);
  const sessionsRef = useRef(sessions);
  const historySessionsRef = useRef<Session[]>([]);

  useEffect(() => {
    sessionsRef.current = sessions;
  }, [sessions]);

  useEffect(() => {
    historySessionsRef.current = historySessions;
  }, [historySessions]);

  // Invalidate the prior identity before paint. A passive effect would leave a window where an
  // already-resolved request for the previous account could commit private rows into the new view.
  useLayoutEffect(() => {
    if (sessionIdentityRef.current === sessionIdentity) return;

    sessionIdentityRef.current = sessionIdentity;
    loadRequestRef.current += 1;
    sessionsRef.current = [];
    historySessionsRef.current = [];
    setSessions([]);
    setHistorySessions([]);
    setLoadError(null);
    setLoading(true);
  }, [sessionIdentity, setLoading]);

  const loadSessions = useCallback(
    async (showLoading = false) => {
      const requestedIdentity = sessionIdentity;
      const request = ++loadRequestRef.current;

      // Real people are signed in behind this list. Mock mode shows no sessions rather than
      // putting their addresses and sign-in times on a screen meant to hold generated data.
      if (mockMode) {
        sessionsRef.current = [];
        historySessionsRef.current = [];
        setSessions([]);
        setHistorySessions([]);
        setLoadError(null);
        markLoaded();
        return;
      }
      try {
        if (
          showLoading &&
          sessionsRef.current.length === 0 &&
          historySessionsRef.current.length === 0
        ) {
          setLoading(true);
        }
        // The server pages over ALL active sessions (pageSize capped at 100), so load every
        // active session, following server pages when needed. History rides along on the first
        // response only.
        const first = await ApiService.getSessions<SessionsResponse>(1, 100);
        // A response missing either array still resolves, so it never reaches the catch below.
        // Falling back to an empty list keeps the page on its empty state instead of throwing
        // when the counts read .length.
        let loadedSessions = first.sessions ?? [];
        const serverPages = first.pagination?.totalPages || 1;
        if (serverPages > 1) {
          const rest = await Promise.all(
            Array.from({ length: serverPages - 1 }, (_: unknown, i: number) =>
              ApiService.getSessions<SessionsResponse>(i + 2, 100)
            )
          );
          loadedSessions = loadedSessions.concat(
            rest.flatMap((r: SessionsResponse) => r.sessions ?? [])
          );
        }
        const loadedHistory = first.historySessions ?? [];
        if (
          request !== loadRequestRef.current ||
          requestedIdentity !== sessionIdentityRef.current
        ) {
          return;
        }

        sessionsRef.current = loadedSessions;
        historySessionsRef.current = loadedHistory;
        setSessions(loadedSessions);
        setHistorySessions(loadedHistory);
        setLoadError(null);
        markLoaded();
      } catch (err: unknown) {
        if (
          request !== loadRequestRef.current ||
          requestedIdentity !== sessionIdentityRef.current
        ) {
          return;
        }

        setLoadError(getErrorMessage(err));
        // A failed first load stays un-loaded, so the next attempt may show the skeleton again
        // and the remembered pick waits instead of being judged against an empty list.
        markFailed();
      }
    },
    [mockMode, sessionIdentity, setLoading, markLoaded, markFailed]
  );

  const handleSessionsStale = useCallback(() => {
    void loadSessions(false);
  }, [loadSessions]);

  const handleSessionDeleted = useCallback((event: { sessionId: string; sessionType: string }) => {
    setSessions((previous: Session[]) => {
      const remaining = previous.filter((session: Session) => session.id !== event.sessionId);
      sessionsRef.current = remaining;
      return remaining;
    });
    setHistorySessions((previous: Session[]) => {
      const remaining = previous.filter((session: Session) => session.id !== event.sessionId);
      historySessionsRef.current = remaining;
      return remaining;
    });
  }, []);

  const handleSessionLastSeenUpdated = useCallback(
    (event: { sessionId: string; lastSeenAt: string }) => {
      setSessions((prev: Session[]) =>
        prev.map((session: Session) =>
          session.id === event.sessionId ? { ...session, lastSeenAt: event.lastSeenAt } : session
        )
      );
    },
    []
  );

  const handlePrefillPermissionChanged = useCallback(
    (event: {
      sessionId: string;
      enabled: boolean;
      prefillExpiresAt?: string;
      service?: string;
    }) => {
      // An absent or unrecognized service id resolves to Steam, matching the events sent
      // before the permission was split into one field per service.
      const service = prefillServiceConfig(event.service ?? '');
      setSessions((prev: Session[]) =>
        prev.map((s: Session) =>
          s.id !== event.sessionId
            ? s
            : {
                ...s,
                [service.sessionEnabledField]: event.enabled,
                [service.sessionExpiresAtField]: event.prefillExpiresAt || null
              }
        )
      );
    },
    []
  );

  // SignalR subscriptions + initial load
  useEffect(() => {
    void loadSessions(true);

    RELOAD_EVENTS.forEach((eventName: string) => on(eventName, handleSessionsStale));
    on('UserSessionDeleted', handleSessionDeleted);
    on('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
    on('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);

    return () => {
      RELOAD_EVENTS.forEach((eventName: string) => off(eventName, handleSessionsStale));
      off('UserSessionDeleted', handleSessionDeleted);
      off('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
      off('GuestPrefillPermissionChanged', handlePrefillPermissionChanged);
    };
  }, [
    loadSessions,
    on,
    off,
    handleSessionsStale,
    handleSessionDeleted,
    handleSessionLastSeenUpdated,
    handlePrefillPermissionChanged
  ]);

  // Recover a stale snapshot after a reconnect: a session change event can be missed while
  // the socket is down, so resync the sessions view whenever the connection returns.
  useReconnectRefetch(isConnected, () => {
    void loadSessions(false);
  });

  // Periodic tick so getSessionStatus() re-evaluates against a fresh "now".
  // Without this, the status dot only changes when an unrelated render fires,
  // producing the "sometimes active / sometimes away when I click" flicker.
  useEffect(() => {
    const id = setInterval(() => {
      setStatusTick((tick: number) => (tick + 1) % 1_000_000);
    }, 15_000);
    return () => clearInterval(id);
  }, []);

  const getSessionStatus = (session: Session): SessionStatus => {
    if (session.isRevoked || session.isExpired) return 'inactive';

    // Presence gate from the unified activity registry (one ActivityUpdated event drives every dot).
    // The backend reconciles this against the database on a timer, so once a snapshot has arrived it is
    // authoritative for every valid session (not just ones created by this process). Before that first
    // snapshot lands, default to present rather than guessing the registry means "gone". The last-seen
    // aging below still decides active vs away.
    const present = activity.isActiveOrFallback('userSession', session.id, 'present', true);
    if (!present) return 'inactive';

    if (session.isCurrentSession && isLocallyActive) {
      return 'active';
    }

    if (!session.lastSeenAt) return 'inactive';

    const now = new Date();
    const lastSeen = parseUtcDate(session.lastSeenAt);
    const diffSeconds = (now.getTime() - lastSeen.getTime()) / 1000;

    if (diffSeconds <= ACTIVE_MAX_AGE_SECONDS) return 'active';
    if (diffSeconds <= AWAY_MAX_AGE_SECONDS) return 'away';
    return 'inactive';
  };

  return {
    sessions,
    historySessions,
    loading,
    loadError,
    loaded,
    reload: loadSessions,
    getSessionStatus
  };
};
