import { useRef, useState, useCallback, useEffect } from 'react';
import { type HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { SIGNALR_BASE, STORAGE_KEYS } from '@utils/constants';
import { sessionStore } from '@utils/storage';
import { formatTimeRemaining, type PrefillSessionDto } from '../types';
import type { DaemonAuthState } from '@/types/operations';
import type { GameServiceId } from '@/types/gameService';
import type { LogEntryType } from '../ActivityLog.utils';
import i18n from '@/i18n';
import { getErrorMessage } from '@utils/error';
import { prefillServiceConfig } from './prefillServiceConfig';
import { registerPrefillEventHandlers } from './usePrefillEventHandlers';
import { InfiniteBackoffRetryPolicy } from '@contexts/SignalRContext/retryPolicy';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useErrorHandler } from '@hooks/useErrorHandler';
import {
  applyPrefillRunProgress,
  canStartPrefill,
  dropUnlistedFinishedRuns,
  isPrefillRunActive,
  mergePrefillRuns,
  supportsConcurrentPrefill,
  type PrefillRun,
  type PrefillProgress
} from './prefillTypes';

// The hub's create answer: the session, or the key and values of the refusal.
type CreateSessionResult =
  | { session: PrefillSessionDto; stageKey: null; context: null }
  | { session: null; stageKey: string; context: Record<string, unknown> | null };

interface UsePrefillSignalROptions {
  onSessionEnd?: () => void;
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  onAuthStateChanged: (state: DaemonAuthState) => void;
  clearAllPrefillStorage: () => void;
  /** Hub path override (default: "/steam-daemon") */
  hubPath?: string;
  /** Service identifier for service-specific event routing */
  serviceId?: GameServiceId;
}

interface UsePrefillSignalRReturn {
  runs: PrefillRun[];
  runErrors: Record<string, string>;
  canStart: boolean;
  refreshRuns: () => Promise<void>;
  // Connection
  hubConnection: React.RefObject<HubConnection | null>;
  isConnecting: boolean;
  isConnected: boolean;
  hubConnectFailed: boolean;
  retryConnection: () => void;

  // Session
  session: PrefillSessionDto | null;
  setSession: React.Dispatch<React.SetStateAction<PrefillSessionDto | null>>;
  timeRemaining: number;
  setTimeRemaining: React.Dispatch<React.SetStateAction<number>>;
  isLoggedIn: boolean;
  setIsLoggedIn: React.Dispatch<React.SetStateAction<boolean>>;
  isInitializing: boolean;
  isCreating: boolean;

  // Progress
  isPrefillActive: boolean;

  // Session management
  createSession: (clearLogs: () => void) => Promise<void>;

  // Error
  /** The last session-create or subscribe failure's reason; null once a new attempt starts. Drawn for guests only; admins get the popup. */
  createSessionError: string | null;

  // Refs for command execution
  expectedAppCountRef: React.RefObject<number>;

  // Cancel orchestration
  cancelPrefill: (runId: string) => Promise<void>;
}

export function usePrefillSignalR(options: UsePrefillSignalROptions): UsePrefillSignalRReturn {
  const {
    onSessionEnd,
    addLog,
    hubPath = '/steam-daemon',
    serviceId = 'steam',
    onAuthStateChanged,
    clearAllPrefillStorage
  } = options;
  const t = i18n.t.bind(i18n);
  // Resolved through t() at call time so log lines follow the active language.
  const serviceNameKey = prefillServiceConfig(serviceId).serviceNameKey;

  // Connection refs
  const hubConnection = useRef<HubConnection | null>(null);
  const initializationAttempted = useRef(false);
  const connectInFlightRef = useRef<Promise<HubConnection | null> | null>(null);
  // False once the hook unmounts, so a connect still in flight stops its socket instead of keeping it.
  const mountedRef = useRef(true);

  // Completion tracking refs
  const expectedAppCountRef = useRef(0);

  // Session ref for SignalR handlers
  const sessionRef = useRef<PrefillSessionDto | null>(null);

  const { notifyError } = useErrorHandler();

  // State
  const [session, setSession] = useState<PrefillSessionDto | null>(null);
  const [runs, setRuns] = useState<PrefillRun[]>([]);
  const [runErrors, setRunErrors] = useState<Record<string, string>>({});
  const runsRef = useRef<PrefillRun[]>([]);
  const runsRequestRef = useRef<Promise<void> | null>(null);
  const runsAgainRef = useRef(false);
  const cancelRunsRef = useRef(new Set<string>());
  const pendingProgressRef = useRef(new Map<string, PrefillProgress>());
  const updateRuns = useCallback((incoming: PrefillRun[]) => {
    const currentSessionId = sessionRef.current?.id;
    runsRef.current = mergePrefillRuns(
      runsRef.current.filter((run) => run.sessionId === currentSessionId),
      incoming.filter((run) => run.sessionId === currentSessionId)
    );
    for (const progress of pendingProgressRef.current.values()) {
      runsRef.current = applyPrefillRunProgress(runsRef.current, progress);
      if (runsRef.current.some((run) => run.runId === progress.operationId))
        pendingProgressRef.current.delete(progress.operationId!);
    }
    setRuns(runsRef.current);
  }, []);
  useEffect(() => {
    runsRef.current = runsRef.current.filter((run) => run.sessionId === session?.id);
    setRuns(runsRef.current);
    setRunErrors({});
    cancelRunsRef.current.clear();
  }, [session?.id]);
  useEffect(() => {
    if (session?.runs) updateRuns(session.runs);
  }, [session, updateRuns]);
  const [isConnecting, setIsConnecting] = useState(false);
  // Reactive "the hub socket is up" flag, driven by the connection lifecycle below. Consumers
  // depend on it to re-run one-shot reads (e.g. the size estimate) once the socket returns.
  const [isConnected, setIsConnected] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [isInitializing, setIsInitializing] = useState(true);
  const [hubConnectFailed, setHubConnectFailed] = useState(false);
  const [createSessionError, setCreateSessionError] = useState<string | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<number>(0);
  const [isLoggedIn, setIsLoggedIn] = useState(false);

  // Keep refs in sync
  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  // Drops a session the server has ended, whether it said so or no longer lists it for this page.
  const endSession = useCallback(() => {
    setSession(null);
    setIsLoggedIn(false);
    clearAllPrefillStorage();
    onSessionEnd?.();
  }, [clearAllPrefillStorage, onSessionEnd]);

  // Reads the session's run list (GetPrefillRuns) and binds the live bar; called after subscribe, on
  // reconnect and on visibilitychange so the bar does not wait for the next broadcast tick.
  const rehydratePrefillProgress = useCallback(
    async (connection: HubConnection, sessionId: string): Promise<void> => {
      if (supportsConcurrentPrefill(sessionRef.current) || runsRef.current.length > 0) {
        if (runsRequestRef.current) {
          runsAgainRef.current = true;
          return runsRequestRef.current;
        }
        const request = (async () => {
          try {
            do {
              runsAgainRef.current = false;
              const currentRuns = await connection.invoke<PrefillRun[]>(
                'GetPrefillRuns',
                sessionId
              );
              if (sessionRef.current?.id !== sessionId) return;
              runsRef.current = dropUnlistedFinishedRuns(runsRef.current, currentRuns);
              updateRuns(currentRuns);
            } while (runsAgainRef.current);
          } catch (error: unknown) {
            if (sessionRef.current?.id !== sessionId) return;
            updateRuns(
              runsRef.current.map((run) =>
                isPrefillRunActive(run) ? { ...run, recovering: true } : run
              )
            );
            console.warn('Prefill run refresh:', getErrorMessage(error));
          } finally {
            runsRequestRef.current = null;
          }
        })();
        runsRequestRef.current = request;
        return request;
      }
    },
    [updateRuns]
  );

  const refreshRuns = useCallback(async () => {
    const connection = hubConnection.current;
    const currentSession = sessionRef.current;
    if (connection && currentSession) await rehydratePrefillProgress(connection, currentSession.id);
  }, [rehydratePrefillProgress]);

  const applyRunProgress = useCallback(
    (progress: PrefillProgress) => {
      const existing = runsRef.current.find((run) => run.runId === progress.operationId);
      if (!existing) {
        const previous = pendingProgressRef.current.get(progress.operationId!);
        if (!previous || (progress.sequence ?? -1) > (previous.sequence ?? -1))
          pendingProgressRef.current.set(progress.operationId!, progress);
        void refreshRuns();
        return;
      }
      runsRef.current = applyPrefillRunProgress(runsRef.current, progress);
      setRuns(runsRef.current);
      if (['completed', 'failed', 'cancelled'].includes(progress.state)) void refreshRuns();
    },
    [refreshRuns]
  );

  // Cancels one run: marks it cancelling, invokes the run's hub cancel, and reads the run list again.
  const cancelPrefill = useCallback(
    async (runId: string): Promise<void> => {
      const connection = hubConnection.current;
      const currentSession = sessionRef.current;
      if (!connection || !currentSession) return;

      const run = runsRef.current.find((item) => item.runId === runId);
      if (
        !run ||
        !isPrefillRunActive(run) ||
        cancelRunsRef.current.has(runId) ||
        run.cancelRequested
      )
        return;
      cancelRunsRef.current.add(runId);
      updateRuns([{ ...run, cancelRequested: true }]);
      setRunErrors((errors) => ({ ...errors, [runId]: '' }));
      try {
        await connection.invoke('CancelPrefillRunAsync', currentSession.id, runId);
        await refreshRuns();
      } catch (error: unknown) {
        if (sessionRef.current?.id !== currentSession.id) return;
        runsRef.current = runsRef.current.map((item) =>
          item.runId === runId ? { ...item, cancelRequested: false } : item
        );
        setRuns(runsRef.current);
        setRunErrors((errors) => ({ ...errors, [runId]: t('prefill.log.failedCancelPrefill') }));
        console.warn('Prefill cancellation:', getErrorMessage(error));
      } finally {
        cancelRunsRef.current.delete(runId);
      }
    },
    [t, updateRuns, refreshRuns]
  );

  const connectToHub = useCallback(async (): Promise<HubConnection | null> => {
    // Serialize concurrent connection attempts - only one connection should be created
    if (connectInFlightRef.current) {
      return await connectInFlightRef.current;
    }

    const connectPromise = (async (): Promise<HubConnection | null> => {
      // Reuse existing connection if already connected
      if (hubConnection.current?.state === 'Connected') {
        return hubConnection.current;
      }

      // Stop any existing connection before creating a new one
      if (hubConnection.current) {
        try {
          await hubConnection.current.stop();
        } catch {
          // Ignore errors stopping old connection
        }
        hubConnection.current = null;
      }

      setIsConnecting(true);

      try {
        const connection = new HubConnectionBuilder()
          .withUrl(`${SIGNALR_BASE}${hubPath}`)
          // The default policy stops after four tries (~30s) and nothing here rebuilds a closed
          // connection, so a container restart left prefill progress dead until a page reload.
          // Always-visible rather than the main hub's page-visibility check: that check pauses
          // retries while the tab is hidden and relies on the visibility handler to reconnect,
          // and this hub's handler re-syncs an already-Connected socket instead of rebuilding one.
          .withAutomaticReconnect(new InfiniteBackoffRetryPolicy(() => true))
          .configureLogging(LogLevel.Information)
          .build();

        // Register all event handlers
        registerPrefillEventHandlers(connection, {
          applyRunProgress,
          addLog,
          onAuthStateChanged,
          setSession,
          setTimeRemaining,
          setIsLoggedIn,
          endSession,
          sessionRef,
          serviceId,
          rehydratePrefillProgress
        });

        // Keep the reactive isConnected flag in step with the socket. These handlers are
        // additive to the ones registerPrefillEventHandlers registers, so both run.
        connection.onreconnecting(() => {
          setIsConnected(false);
          updateRuns(
            runsRef.current.map((run) =>
              isPrefillRunActive(run) ? { ...run, recovering: true } : run
            )
          );
        });
        connection.onreconnected(() => setIsConnected(true));
        connection.onclose(() => setIsConnected(false));

        await connection.start();
        if (!mountedRef.current) {
          connection.stop().catch((error: unknown) => console.warn('Hub cleanup:', error));
          return null;
        }
        hubConnection.current = connection;
        setIsConnecting(false);
        setIsConnected(true);
        return connection;
      } catch {
        setHubConnectFailed(true);
        setIsConnecting(false);
        setIsConnected(false);
        return null;
      }
    })();

    connectInFlightRef.current = connectPromise;
    const result = await connectPromise;
    connectInFlightRef.current = null;
    return result;
  }, [
    addLog,
    onAuthStateChanged,
    endSession,
    serviceId,
    hubPath,
    rehydratePrefillProgress,
    applyRunProgress,
    updateRuns
  ]);

  const initializeSession = useCallback(async () => {
    if (initializationAttempted.current) return;
    initializationAttempted.current = true;
    setHubConnectFailed(false);

    setIsInitializing(true);

    try {
      const connection = await connectToHub();
      if (!connection) {
        setIsInitializing(false);
        return;
      }

      // Adoption of an already-running server session runs once on mount. A transient hub hiccup
      // here (common on mobile, where the socket has just come up) used to be swallowed silently,
      // permanently missing an existing active session until the page was reloaded. Retry the
      // lookup a bounded number of times on a THROW (never on a legitimately empty result, so a
      // user with no session still reaches the home page immediately) before giving up.
      let existingSessions: PrefillSessionDto[] | undefined;
      for (let attempt = 0; ; attempt++) {
        try {
          existingSessions = await connection.invoke<PrefillSessionDto[]>('GetMySessions');
          break;
        } catch (err) {
          // Retry through a transient outage regardless of state: a mobile hiccup flips the
          // socket to 'Reconnecting' (invoking then throws), and withAutomaticReconnect usually
          // restores it within this window - so back off and try again rather than bailing on the
          // non-Connected state, which used to permanently miss an existing session.
          if (attempt >= 4) throw err;
          await new Promise((resolve) => setTimeout(resolve, 500 * (attempt + 1)));
        }
      }
      const activeSession = existingSessions.find((s) => s.status === 'Active');

      if (activeSession) {
        addLog(
          'info',
          t('prefill.log.reconnectingExistingSession'),
          t('prefill.log.sessionDetail', { id: activeSession.id })
        );
        await connection.invoke('SubscribeToSessionAsync', activeSession.id);

        setSession(activeSession);
        sessionRef.current = activeSession;
        setTimeRemaining(
          Math.max(0, Math.floor((Date.parse(activeSession.expiresAt) - Date.now()) / 1000))
        );
        setIsLoggedIn(activeSession.authState === 'Authenticated');

        // Server truth: a prefill is already running on the daemon. Seed the runs the session
        // carries, then read the run list. This is what fixes the "already-running prefill shows
        // no bar / no cancel" core bug.
        if (supportsConcurrentPrefill(activeSession)) {
          updateRuns(activeSession.runs ?? []);
          await rehydratePrefillProgress(connection, activeSession.id);
          return;
        }

        addLog(
          'success',
          t('prefill.log.reconnectedExistingSession'),
          t('prefill.log.containerDetail', { name: activeSession.containerName })
        );
        // A session with no time left gets no "expires in" line; the expired panel says it.
        const secondsLeft = Math.floor((Date.parse(activeSession.expiresAt) - Date.now()) / 1000);
        if (secondsLeft > 0) {
          addLog(
            'info',
            t('prefill.log.sessionExpiresIn', { time: formatTimeRemaining(secondsLeft) })
          );
        }

        if (activeSession.authState === 'Authenticated' && serviceId !== 'battlenet') {
          // Battle.net is anonymous - never logs a "logged in" message
          addLog('info', t('prefill.log.alreadyLoggedIn', { service: t(serviceNameKey) }));
        } else if (serviceId !== 'battlenet') {
          // Battle.net is anonymous - no login prompt needed
          addLog('info', t('prefill.log.loginPrompt', { service: t(serviceNameKey) }));
        }
      } else {
        // No active session found: check for stale storage data from a previous session.
        // This happens when the server was stopped/restarted and cleared sessions
        const hasStaleData =
          sessionStore.getItem(STORAGE_KEYS.PREFILL_SESSION_ID) ||
          sessionStore.getItem('prefill_activity_log');
        if (hasStaleData) {
          clearAllPrefillStorage();
        }
      }
    } catch (err) {
      // Background session-reconciliation on mount/reconnect - a hub-closed/access-denied scenario
      // is expected and stays silent. A genuine failure means a running session may have been
      // missed, so it shows the connection box with Retry instead of empty platform cards.
      const errorMessage = err instanceof Error ? err.message : String(err);
      const isExpectedHubClose =
        errorMessage.includes('connection being closed') ||
        errorMessage.includes('Invocation canceled');
      if (!isExpectedHubClose) {
        console.error('[usePrefillSignalR] Failed to initialize session:', getErrorMessage(err));
        setHubConnectFailed(true);
      }
    } finally {
      setIsInitializing(false);
    }
  }, [
    connectToHub,
    addLog,
    clearAllPrefillStorage,
    serviceId,
    serviceNameKey,
    t,
    rehydratePrefillProgress,
    updateRuns
  ]);

  // Retry reconnects and adopts a running session, the same as the first mount.
  const retryConnection = useCallback(() => {
    initializationAttempted.current = false;
    void initializeSession();
  }, [initializeSession]);

  const createSession = useCallback(
    async (clearLogs: () => void) => {
      setIsCreating(true);
      setHubConnectFailed(false);
      setCreateSessionError(null);
      clearLogs();

      try {
        let connection = hubConnection.current;
        if (!connection || connection.state !== 'Connected') {
          connection = await connectToHub();
        }

        if (!connection) {
          // connectToHub's failure already shows the connection box with Retry, the one notice for it.
          setIsCreating(false);
          return;
        }

        addLog('info', t('prefill.log.creatingSession'));

        const created = await connection.invoke<CreateSessionResult>('OpenSessionAsync');
        if (created.stageKey !== null) {
          // A refused create carries its reason as a key and values, shown through the failure path below.
          throw new Error(t(created.stageKey, created.context ?? {}));
        }
        const session = created.session;
        setSession(session);
        setTimeRemaining(
          Math.max(0, Math.floor((Date.parse(session.expiresAt) - Date.now()) / 1000))
        );

        const isExistingSession = session.authState === 'Authenticated';
        setIsLoggedIn(isExistingSession);

        if (isExistingSession) {
          addLog(
            'success',
            t('prefill.log.connectedExistingSession'),
            t('prefill.log.containerDetail', { name: session.containerName })
          );
          if (serviceId !== 'battlenet') {
            // Battle.net is anonymous - no "logged in" message
            addLog('info', t('prefill.log.alreadyLoggedIn', { service: t(serviceNameKey) }));
          }
        } else {
          addLog(
            'success',
            t('prefill.log.sessionCreated'),
            t('prefill.log.containerDetail', { name: session.containerName })
          );
          if (serviceId !== 'battlenet') {
            // Battle.net is anonymous - no login required before prefill
            addLog('info', t('prefill.log.loginBeforePrefill', { service: t(serviceNameKey) }));
          }
        }
        // An existing session handed back after its expiry gets no "expires in" line.
        const secondsLeft = Math.floor((Date.parse(session.expiresAt) - Date.now()) / 1000);
        if (secondsLeft > 0) {
          addLog(
            'info',
            t('prefill.log.sessionExpiresIn', { time: formatTimeRemaining(secondsLeft) })
          );
        }

        await connection.invoke('SubscribeToSessionAsync', session.id);
        setIsCreating(false);
      } catch (err) {
        const errorMessage = getErrorMessage(err);
        setCreateSessionError(errorMessage);
        notifyError(t('prefill.errors.failedCreateSession'), err);
        addLog('error', errorMessage);
        setIsCreating(false);
      }
    },
    [connectToHub, addLog, serviceId, serviceNameKey, t, notifyError]
  );

  // Initialize on mount, cleanup on unmount
  useEffect(() => {
    initializeSession();

    return () => {
      mountedRef.current = false;
      if (hubConnection.current) {
        hubConnection.current.stop().catch((error: unknown) => console.warn('Hub cleanup:', error));
        hubConnection.current = null;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // If the socket genuinely reconnects and we still never adopted a server session (the initial
  // GetMySessions lost the race on a flaky mobile connect and its bounded retries gave up),
  // re-run adoption now that the hub is healthy. Fires once per connect, and does nothing unless
  // adoption never completed. An already-adopted session is reconciled by the onreconnected
  // handler, so this only acts when there is no current session and initialization is not already
  // in flight.
  useReconnectRefetch(isConnected, () => {
    if (!sessionRef.current && !isInitializing) {
      initializationAttempted.current = false;
      void initializeSession();
    }
  });

  // Re-sync when the tab becomes visible again (handles backgrounded tab / mobile suspend where
  // the socket stays nominally alive so onreconnected never fires). Mirrors the visibilitychange
  // pattern in SpeedContext/SignalRContext: re-subscribe, then re-read the run list. Small delay
  // lets any pending SignalR reconnect settle first.
  useEffect(() => {
    // V8: track the inner 500ms timeout so it can be cleared on unmount (and on re-fire) — without
    // this a post-unmount callback could invoke hub methods / setState after teardown.
    let visibilityTimeoutId: ReturnType<typeof setTimeout> | null = null;

    const handleVisibilityChange = () => {
      if (document.hidden) return;
      const connection = hubConnection.current;
      const currentSession = sessionRef.current;
      if (!connection || connection.state !== 'Connected' || !currentSession) return;

      if (visibilityTimeoutId !== null) {
        clearTimeout(visibilityTimeoutId);
      }
      visibilityTimeoutId = setTimeout(() => {
        visibilityTimeoutId = null;
        const conn = hubConnection.current;
        const sess = sessionRef.current;
        if (!conn || conn.state !== 'Connected' || !sess) return;
        void (async () => {
          try {
            await conn.invoke('SubscribeToSessionAsync', sess.id);
          } catch {
            // A failed or slow reply does not end the session; only a server that no longer lists it has
            // ended it. A session it still lists is subscribed once more, because run events go only to
            // subscribers.
            if (conn.state !== 'Connected') return;
            try {
              const listed = await conn.invoke<PrefillSessionDto[]>('GetMySessions');
              if (!listed.some((entry: PrefillSessionDto) => entry.id === sess.id)) {
                addLog('warning', t('prefill.log.sessionEndedOnServer'));
                endSession();
                return;
              }
              await conn.invoke('SubscribeToSessionAsync', sess.id);
            } catch {
              // The next reconnect or visibility change tries again.
              return;
            }
          }
          try {
            await rehydratePrefillProgress(conn, sess.id);
          } catch {
            // Non-critical: next live tick will resync.
          }
        })();
      }, 500);
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      if (visibilityTimeoutId !== null) {
        clearTimeout(visibilityTimeoutId);
        visibilityTimeoutId = null;
      }
    };
  }, [rehydratePrefillProgress, t, addLog, endSession]);

  return {
    runs,
    runErrors,
    canStart:
      !!session &&
      isConnected &&
      canStartPrefill({
        ...session,
        runs,
        activeRunCount:
          runs.length > 0 ? runs.filter(isPrefillRunActive).length : session.activeRunCount
      }),
    refreshRuns,
    // Connection
    hubConnection,
    isConnecting,
    isConnected,
    hubConnectFailed,
    retryConnection,

    // Session
    session,
    setSession,
    timeRemaining,
    setTimeRemaining,
    isLoggedIn,
    setIsLoggedIn,
    isInitializing,
    isCreating,

    // Progress
    isPrefillActive: runs.some(isPrefillRunActive),

    // Session management
    createSession,

    // Error
    createSessionError,

    // Refs
    expectedAppCountRef,

    // Cancel orchestration
    cancelPrefill
  };
}
