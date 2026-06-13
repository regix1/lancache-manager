import { useRef, useState, useCallback, useEffect } from 'react';
import { type HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { SIGNALR_BASE } from '@utils/constants';
import { formatDurationFromSeconds, formatTimeRemaining, type PrefillSessionDto } from '../types';
import type { DaemonAuthState } from '@/types/operations';
import type { LogEntryType } from '../ActivityLog.utils';
import i18n from '../../../../i18n';
import { usePrefillAnimation } from './usePrefillAnimation';
import { registerPrefillEventHandlers } from './usePrefillEventHandlers';
import {
  PREFILL_SESSION_TIMEOUT_MS,
  COMPLETION_NOTIFICATION_WINDOW_MS,
  CANCEL_WATCHDOG_MS
} from './prefillConstants';
import type { PrefillProgress, BackgroundCompletion } from './prefillTypes';

interface UsePrefillSignalROptions {
  onSessionEnd?: () => void;
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  setBackgroundCompletion: (completion: BackgroundCompletion) => void;
  clearBackgroundCompletion: () => void;
  isCompletionDismissed: (completedAt: string) => boolean;
  onAuthStateChanged: (state: DaemonAuthState) => void;
  clearAllPrefillStorage: () => void;
  /** Hub path override (default: "/steam-daemon") */
  hubPath?: string;
  /** Service identifier for service-specific event routing */
  serviceId?: string;
}

interface UsePrefillSignalRReturn {
  // Connection
  hubConnection: React.RefObject<HubConnection | null>;
  isConnecting: boolean;

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
  prefillProgress: PrefillProgress | null;
  setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>;
  isPrefillActive: boolean;
  setIsPrefillActive: React.Dispatch<React.SetStateAction<boolean>>;

  // Session management
  createSession: (clearLogs: () => void) => Promise<void>;

  // Error
  error: string | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;

  // Refs for command execution
  isCancelling: React.RefObject<boolean>;
  expectedAppCountRef: React.RefObject<number>;

  // Cancel orchestration
  cancelPrefill: () => Promise<void>;
  isCancellingState: boolean;
}

export function usePrefillSignalR(options: UsePrefillSignalROptions): UsePrefillSignalRReturn {
  const {
    onSessionEnd,
    addLog,
    hubPath = '/steam-daemon',
    serviceId = 'steam',
    setBackgroundCompletion,
    clearBackgroundCompletion,
    isCompletionDismissed,
    onAuthStateChanged,
    clearAllPrefillStorage
  } = options;
  const t = i18n.t.bind(i18n);

  // Connection refs
  const hubConnection = useRef<HubConnection | null>(null);
  const initializationAttempted = useRef(false);
  const isCancelling = useRef(false);
  const connectInFlightRef = useRef<Promise<HubConnection | null> | null>(null);
  // Watchdog timer started when Cancel is clicked; cleared by any terminal event.
  const cancelWatchdogRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Completion tracking refs
  const expectedAppCountRef = useRef(0);

  // Counters for prefill summary logging
  const downloadedGamesCountRef = useRef(0);
  const cachedGamesCountRef = useRef(0);
  const totalBytesDownloadedRef = useRef(0);

  // Ref for setBackgroundCompletion (avoids stale closure)
  const setBackgroundCompletionRef = useRef(setBackgroundCompletion);

  // Session ref for SignalR handlers
  const sessionRef = useRef<PrefillSessionDto | null>(null);

  // Animation hook
  const {
    currentAnimationAppIdRef,
    cachedAnimationQueueRef,
    isProcessingAnimationRef,
    resetAnimationState,
    stopAnimations,
    enqueueAnimation
  } = usePrefillAnimation();

  // State
  const [session, setSession] = useState<PrefillSessionDto | null>(null);
  const [isConnecting, setIsConnecting] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [isInitializing, setIsInitializing] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<number>(0);
  const [isLoggedIn, setIsLoggedIn] = useState(false);
  // PAINT-BEFORE-NETWORK HINT ONLY. The authoritative source of "is a prefill running" is the
  // server `session.isPrefilling` flag (re-hydrated in initializeSession / SessionSubscribed /
  // visibilitychange via GetCurrentPrefillProgress). This sessionStorage window merely lets the
  // bar paint instantly on reload before the hub round-trips; initializeSession reconciles it
  // against server truth and clears it if the daemon is no longer prefilling.
  const [isPrefillActive, setIsPrefillActive] = useState<boolean>(() => {
    try {
      const inProgress = sessionStorage.getItem('prefill_in_progress');
      if (inProgress) {
        const parsed = JSON.parse(inProgress);
        const startedAt = new Date(parsed.startedAt).getTime();
        if (Date.now() - startedAt < PREFILL_SESSION_TIMEOUT_MS) {
          return true;
        }
      }
    } catch {
      /* ignore */
    }
    return false;
  });
  // Same paint-before-network hint: a transient "reconnecting" placeholder so the bar shows
  // immediately on reload. Replaced by the real GetCurrentPrefillProgress snapshot (or the first
  // live tick), and cleared by initializeSession when the server reports the daemon is idle.
  const [prefillProgress, setPrefillProgress] = useState<PrefillProgress | null>(() => {
    try {
      const inProgress = sessionStorage.getItem('prefill_in_progress');
      if (inProgress) {
        const parsed = JSON.parse(inProgress);
        const startedAt = new Date(parsed.startedAt).getTime();
        if (Date.now() - startedAt < PREFILL_SESSION_TIMEOUT_MS) {
          // Return a placeholder progress to show "reconnecting" state
          return {
            state: 'reconnecting',
            message: undefined,
            currentAppId: '',
            currentAppName: undefined,
            percentComplete: 0,
            bytesDownloaded: 0,
            totalBytes: 0,
            bytesPerSecond: 0,
            elapsedSeconds: Math.floor((Date.now() - startedAt) / 1000)
          };
        }
      }
    } catch {
      /* ignore */
    }
    return null;
  });
  // Reactive mirror of the isCancelling ref, so the Cancel button can show a disabled
  // "Cancelling..." state (the ref alone doesn't trigger a re-render).
  const [isCancellingState, setIsCancellingState] = useState(false);

  // Keep refs in sync
  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  useEffect(() => {
    setBackgroundCompletionRef.current = setBackgroundCompletion;
  }, [setBackgroundCompletion]);

  // Builds a coarse "reconnecting" progress placeholder from the server session DTO. The DTO
  // carries no percent, so we render the indeterminate 'reconnecting' state until the real
  // GetCurrentPrefillProgress snapshot (or the next live tick) arrives. NEVER paints a bogus 0%.
  const seedReconnectingProgressFromSession = useCallback(
    (sessionDto: PrefillSessionDto): PrefillProgress => ({
      state: 'reconnecting',
      message: undefined,
      currentAppId: sessionDto.currentAppId ?? '',
      currentAppName: sessionDto.currentAppName,
      percentComplete: 0,
      // Keep this placeholder internally consistent: totalBytes is 0 here (the DTO carries no
      // total), so bytesDownloaded must be 0 too — the real numbers arrive with the snapshot /
      // first live tick. Seeding from totalBytesTransferred would make a self-inconsistent
      // "X / 0 B" object (O8).
      bytesDownloaded: 0,
      totalBytes: 0,
      bytesPerSecond: 0,
      elapsedSeconds: 0
    }),
    []
  );

  // CONTRACT: GetCurrentPrefillProgress(sessionId) returns the live PrefillProgress snapshot when
  // the session is prefilling, else null. Called after subscribe / on reconnect / on
  // visibilitychange to bind the real bar without waiting for the next broadcast tick.
  const rehydratePrefillProgress = useCallback(
    async (connection: HubConnection, sessionId: string): Promise<void> => {
      try {
        const snapshot = (await connection.invoke('GetCurrentPrefillProgress', sessionId)) as
          | (PrefillProgress & { totalApps?: number })
          | null;

        if (snapshot) {
          // Live prefill confirmed by the server: bind the real bar (and never below the
          // reconnecting placeholder it replaces). A final state here means it just finished.
          const isFinal =
            snapshot.state === 'completed' ||
            snapshot.state === 'failed' ||
            snapshot.state === 'cancelled' ||
            snapshot.state === 'idle';
          if (isFinal) {
            setIsPrefillActive(false);
            setPrefillProgress(null);
            return;
          }
          if (snapshot.totalApps && snapshot.totalApps > 0) {
            expectedAppCountRef.current = snapshot.totalApps;
          }
          setIsPrefillActive(true);
          setPrefillProgress({
            ...snapshot,
            expectedAppCount: expectedAppCountRef.current || snapshot.totalApps || undefined
          });
        }
        // snapshot === null → not prefilling per server; leave the (placeholder) state for the
        // GetLastPrefillResult reconciliation path to clear so we don't fight it here.
      } catch {
        // Non-critical: the next live PrefillProgress tick will still bind the bar.
      }
    },
    [expectedAppCountRef]
  );

  // Cancel orchestration: hard-stop the local animation queue (so a cancelled prefill can't keep
  // painting), flip the reactive "Cancelling..." button state, invoke the session-scoped hub
  // cancel, and arm a watchdog that force-clears the bar if no terminal event arrives. Terminal
  // events (handled in usePrefillEventHandlers) clear the watchdog + cancelling state.
  const cancelPrefill = useCallback(async (): Promise<void> => {
    const connection = hubConnection.current;
    const currentSession = sessionRef.current;
    if (!connection || !currentSession) return;

    isCancelling.current = true;
    setIsCancellingState(true);
    stopAnimations();
    addLog('info', t('prefill.log.cancellingPrefill'));

    if (cancelWatchdogRef.current !== null) {
      clearTimeout(cancelWatchdogRef.current);
    }
    cancelWatchdogRef.current = setTimeout(() => {
      cancelWatchdogRef.current = null;
      // No terminal event arrived in time - force the bar away so the UI can't get stuck.
      stopAnimations();
      setPrefillProgress(null);
      setIsPrefillActive(false);
      setIsCancellingState(false);
      isCancelling.current = false;
      try {
        sessionStorage.removeItem('prefill_in_progress');
      } catch {
        /* ignore */
      }
    }, CANCEL_WATCHDOG_MS);

    try {
      await connection.invoke('CancelPrefillAsync', currentSession.id);
    } catch {
      isCancelling.current = false;
      setIsCancellingState(false);
      if (cancelWatchdogRef.current !== null) {
        clearTimeout(cancelWatchdogRef.current);
        cancelWatchdogRef.current = null;
      }
      addLog('error', t('prefill.log.failedCancelPrefill'));
    }
  }, [stopAnimations, addLog, t]);

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
      setError(null);

      try {
        const connection = new HubConnectionBuilder()
          .withUrl(`${SIGNALR_BASE}${hubPath}`)
          .withAutomaticReconnect()
          .configureLogging(LogLevel.Information)
          .build();

        // Register all event handlers
        registerPrefillEventHandlers(connection, {
          addLog,
          onAuthStateChanged,
          setSession,
          setTimeRemaining,
          setIsLoggedIn,
          setIsPrefillActive,
          setPrefillProgress,
          onSessionEnd,
          clearAllPrefillStorage,
          setBackgroundCompletionRef,
          clearBackgroundCompletion,
          isCompletionDismissed,
          sessionRef,
          isCancelling,
          currentAnimationAppIdRef,
          expectedAppCountRef,
          downloadedGamesCountRef,
          cachedGamesCountRef,
          totalBytesDownloadedRef,
          enqueueAnimation,
          resetAnimationState,
          stopAnimations,
          cachedAnimationQueueRef,
          isProcessingAnimationRef,
          serviceId,
          rehydratePrefillProgress,
          seedReconnectingProgressFromSession,
          setIsCancellingState,
          cancelWatchdogRef
        });

        await connection.start();
        hubConnection.current = connection;
        setIsConnecting(false);
        return connection;
      } catch {
        setError(t('prefill.errors.failedConnect'));
        setIsConnecting(false);
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
    onSessionEnd,
    clearBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage,
    enqueueAnimation,
    resetAnimationState,
    stopAnimations,
    serviceId,
    hubPath,
    t,
    cachedAnimationQueueRef,
    currentAnimationAppIdRef,
    isProcessingAnimationRef,
    rehydratePrefillProgress,
    seedReconnectingProgressFromSession
  ]);

  const initializeSession = useCallback(async () => {
    if (initializationAttempted.current) return;
    initializationAttempted.current = true;

    setIsInitializing(true);

    try {
      const connection = await connectToHub();
      if (!connection) {
        setIsInitializing(false);
        return;
      }

      const existingSessions = await connection.invoke<PrefillSessionDto[]>('GetMySessions');
      const activeSession = existingSessions?.find((s) => s.status === 'Active');

      if (activeSession) {
        addLog(
          'info',
          t('prefill.log.reconnectingExistingSession'),
          t('prefill.log.sessionDetail', { id: activeSession.id })
        );
        await connection.invoke('SubscribeToSessionAsync', activeSession.id);

        setSession(activeSession);
        setTimeRemaining(activeSession.timeRemainingSeconds);
        setIsLoggedIn(activeSession.authState === 'Authenticated');

        // Server truth: a prefill is already running on the daemon. Seed the bar immediately
        // (state 'reconnecting' until the snapshot binds), then fetch the live snapshot. This
        // is what fixes the "already-running prefill shows no bar / no cancel" core bug.
        if (activeSession.isPrefilling) {
          setIsPrefillActive(true);
          setPrefillProgress(seedReconnectingProgressFromSession(activeSession));
          await rehydratePrefillProgress(connection, activeSession.id);
        }

        addLog(
          'success',
          t('prefill.log.reconnectedExistingSession'),
          t('prefill.log.containerDetail', { name: activeSession.containerName })
        );
        addLog(
          'info',
          t('prefill.log.sessionExpiresIn', {
            time: formatTimeRemaining(activeSession.timeRemainingSeconds)
          })
        );

        if (activeSession.authState === 'Authenticated' && serviceId !== 'battlenet') {
          // Battle.net is anonymous - never logs a "logged in" message
          addLog('info', t('prefill.log.alreadyLoggedIn'));
        } else if (serviceId !== 'battlenet') {
          // Battle.net is anonymous - no login prompt needed
          addLog(
            'info',
            serviceId === 'epic'
              ? t(
                  'prefill.log.loginToEpicPrompt',
                  'Please log in to Epic Games to start prefilling'
                )
              : t('prefill.log.loginToSteamPrompt')
          );
        }

        // Check for missed completions
        try {
          const lastResult = (await connection.invoke(
            'GetLastPrefillResult',
            activeSession.id
          )) as {
            status: string;
            completedAt: string;
            durationSeconds: number;
          } | null;

          if (lastResult && lastResult.status === 'completed') {
            const completedTime = new Date(lastResult.completedAt).getTime();
            // Prefill completed - clear the "reconnecting" progress state
            setIsPrefillActive(false);
            setPrefillProgress(null);
            try {
              sessionStorage.removeItem('prefill_in_progress');
            } catch {
              /* ignore */
            }

            if (Date.now() - completedTime < COMPLETION_NOTIFICATION_WINDOW_MS) {
              const currentBgCompletion = sessionStorage.getItem('prefill_background_completion');
              if (!currentBgCompletion && !isCompletionDismissed(lastResult.completedAt)) {
                const formattedDuration = formatDurationFromSeconds(lastResult.durationSeconds);
                setBackgroundCompletion({
                  completedAt: lastResult.completedAt,
                  message: t('prefill.completion.message', { duration: formattedDuration }),
                  duration: lastResult.durationSeconds
                });
                addLog(
                  'success',
                  t('prefill.log.prefillCompletedWhileAway', { duration: formattedDuration })
                );
              }
            }
          } else if (
            lastResult &&
            (lastResult.status === 'failed' || lastResult.status === 'cancelled')
          ) {
            // Prefill failed or cancelled - clear the "reconnecting" progress state
            setIsPrefillActive(false);
            setPrefillProgress(null);
            try {
              sessionStorage.removeItem('prefill_in_progress');
            } catch {
              /* ignore */
            }
          }
          // If lastResult is null or status is 'in_progress' AND the server says we are NOT
          // prefilling, reconcile away any stale paint-before-network placeholder so the bar
          // doesn't linger in a fake "reconnecting" state. When isPrefilling is true the
          // GetCurrentPrefillProgress rehydrate above already bound the real bar.
          else if (!activeSession.isPrefilling) {
            setIsPrefillActive(false);
            setPrefillProgress(null);
            try {
              sessionStorage.removeItem('prefill_in_progress');
            } catch {
              /* ignore */
            }
          }
        } catch {
          // Non-critical
        }
      } else {
        // No active session found - clear any "reconnecting" progress state
        setIsPrefillActive(false);
        setPrefillProgress(null);

        // Also check if there's stale storage data from a previous session
        // This happens when the server was stopped/restarted and cleared sessions
        const hasStaleData =
          sessionStorage.getItem('prefill_session_id') ||
          sessionStorage.getItem('prefill_activity_log') ||
          sessionStorage.getItem('prefill_in_progress');
        if (hasStaleData) {
          clearAllPrefillStorage();
        }
      }
    } catch (err) {
      // Check if this is a normal access-denied/connection closed scenario
      const errorMessage = err instanceof Error ? err.message : String(err);
      if (
        errorMessage.includes('connection being closed') ||
        errorMessage.includes('Invocation canceled')
      ) {
        // Hub connection closed - access denied or hub unavailable
      } else {
        // Failed to initialize session
      }
    } finally {
      setIsInitializing(false);
    }
  }, [
    connectToHub,
    addLog,
    setBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage,
    serviceId,
    t,
    rehydratePrefillProgress,
    seedReconnectingProgressFromSession
  ]);

  const createSession = useCallback(
    async (clearLogs: () => void) => {
      setIsCreating(true);
      setError(null);
      clearLogs();

      try {
        let connection = hubConnection.current;
        if (!connection || connection.state !== 'Connected') {
          connection = await connectToHub();
        }

        if (!connection) {
          throw new Error(t('prefill.errors.failedEstablishConnection'));
        }

        addLog('info', t('prefill.log.creatingSession'));

        const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSessionAsync');
        setSession(sessionDto);
        setTimeRemaining(sessionDto.timeRemainingSeconds);

        const isExistingSession = sessionDto.authState === 'Authenticated';
        setIsLoggedIn(isExistingSession);

        if (isExistingSession) {
          addLog(
            'success',
            t('prefill.log.connectedExistingSession'),
            t('prefill.log.containerDetail', { name: sessionDto.containerName })
          );
          if (serviceId !== 'battlenet') {
            // Battle.net is anonymous - no "logged in" message
            addLog('info', t('prefill.log.alreadyLoggedIn'));
          }
        } else {
          addLog(
            'success',
            t('prefill.log.sessionCreated'),
            t('prefill.log.containerDetail', { name: sessionDto.containerName })
          );
          if (serviceId !== 'battlenet') {
            // Battle.net is anonymous - no login required before prefill
            addLog(
              'info',
              serviceId === 'epic'
                ? t(
                    'prefill.log.loginToEpicBeforePrefill',
                    'Please log in to Epic Games before starting prefill'
                  )
                : t('prefill.log.loginToSteamBeforePrefill')
            );
          }
        }
        addLog(
          'info',
          t('prefill.log.sessionExpiresIn', {
            time: formatTimeRemaining(sessionDto.timeRemainingSeconds)
          })
        );

        await connection.invoke('SubscribeToSessionAsync', sessionDto.id);
        setIsCreating(false);
      } catch (err) {
        const errorMessage =
          err instanceof Error ? err.message : t('prefill.errors.failedCreateSession');
        setError(errorMessage);
        addLog('error', errorMessage);
        setIsCreating(false);
      }
    },
    [connectToHub, addLog, serviceId, t]
  );

  // Initialize on mount, cleanup on unmount
  useEffect(() => {
    initializeSession();

    return () => {
      // V8: clear the cancel watchdog so its callback can't fire setState after unmount (and so the
      // 5s timer doesn't leak). Terminal events normally clear it, but an unmount within the window
      // after a Cancel would otherwise leave it armed.
      if (cancelWatchdogRef.current !== null) {
        clearTimeout(cancelWatchdogRef.current);
        cancelWatchdogRef.current = null;
      }
      if (hubConnection.current) {
        hubConnection.current.stop().catch((error: unknown) => console.warn('Hub cleanup:', error));
        hubConnection.current = null;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Re-sync when the tab becomes visible again (handles backgrounded tab / mobile suspend where
  // the socket stays nominally alive so onreconnected never fires). Mirrors the visibilitychange
  // pattern in SpeedContext/SignalRContext: re-subscribe, then re-hydrate live progress
  // (GetCurrentPrefillProgress) + missed completion (GetLastPrefillResult). Small delay lets any
  // pending SignalR reconnect settle first.
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
            await rehydratePrefillProgress(conn, sess.id);

            const lastResult = (await conn.invoke('GetLastPrefillResult', sess.id)) as {
              status: string;
              completedAt: string;
              durationSeconds: number;
            } | null;

            if (lastResult && lastResult.status === 'completed') {
              const completedTime = new Date(lastResult.completedAt).getTime();
              if (
                Date.now() - completedTime < COMPLETION_NOTIFICATION_WINDOW_MS &&
                !isCompletionDismissed(lastResult.completedAt)
              ) {
                const formattedDuration = formatDurationFromSeconds(lastResult.durationSeconds);
                setBackgroundCompletionRef.current({
                  completedAt: lastResult.completedAt,
                  message: t('prefill.completion.message', { duration: formattedDuration }),
                  duration: lastResult.durationSeconds
                });
              }
            }
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
  }, [rehydratePrefillProgress, isCompletionDismissed, t]);

  return {
    // Connection
    hubConnection,
    isConnecting,

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
    prefillProgress,
    setPrefillProgress,
    isPrefillActive,
    setIsPrefillActive,

    // Session management
    createSession,

    // Error
    error,
    setError,

    // Refs
    isCancelling,
    expectedAppCountRef,

    // Cancel orchestration
    cancelPrefill,
    isCancellingState
  };
}
