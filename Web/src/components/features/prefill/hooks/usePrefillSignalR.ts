import { useRef, useState, useCallback, useEffect } from 'react';
import { type HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { SIGNALR_BASE } from '@utils/constants';
import authService from '@services/auth.service';
import {
  formatDuration,
  formatTimeRemaining,
  type SteamAuthState,
  type PrefillSessionDto
} from '../types';
import type { LogEntryType } from '../ActivityLog';
import i18n from '../../../../i18n';
import { usePrefillAnimation } from './usePrefillAnimation';
import { registerPrefillEventHandlers } from './usePrefillEventHandlers';
import {
  PREFILL_SESSION_TIMEOUT_MS,
  COMPLETION_NOTIFICATION_WINDOW_MS
} from './prefillConstants';
import type { PrefillProgress, BackgroundCompletion } from './prefillTypes';

interface UsePrefillSignalROptions {
  onSessionEnd?: () => void;
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  setBackgroundCompletion: (completion: BackgroundCompletion) => void;
  clearBackgroundCompletion: () => void;
  isCompletionDismissed: (completedAt: string) => boolean;
  onAuthStateChanged: (state: SteamAuthState) => void;
  clearAllPrefillStorage: () => void;
}

interface UsePrefillSignalRReturn {
  // Connection
  hubConnection: React.RefObject<HubConnection | null>;
  isConnecting: boolean;
  connectToHub: () => Promise<HubConnection | null>;

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
  isPrefillActive: boolean;

  // Session management
  initializeSession: () => Promise<void>;
  createSession: (clearLogs: () => void) => Promise<void>;

  // Error
  error: string | null;
  setError: React.Dispatch<React.SetStateAction<string | null>>;

  // Refs for command execution
  isCancelling: React.RefObject<boolean>;
  expectedAppCountRef: React.RefObject<number>;

  // Session ref for stale closure issues
  sessionRef: React.RefObject<PrefillSessionDto | null>;
}

export function usePrefillSignalR(options: UsePrefillSignalROptions): UsePrefillSignalRReturn {
  const {
    onSessionEnd,
    addLog,
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
  // Initialize isPrefillActive from sessionStorage to immediately show progress UI on page return
  const [isPrefillActive, setIsPrefillActive] = useState<boolean>(() => {
    try {
      const inProgress = sessionStorage.getItem('prefill_in_progress');
      if (inProgress) {
        const parsed = JSON.parse(inProgress);
        // Check if the stored progress is less than 2 hours old (reasonable prefill window)
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
  // Initialize prefillProgress with a "reconnecting" state if we have stored progress
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
            currentAppId: 0,
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

  // Keep refs in sync
  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  useEffect(() => {
    setBackgroundCompletionRef.current = setBackgroundCompletion;
  }, [setBackgroundCompletion]);

  const connectToHub = useCallback(async (): Promise<HubConnection | null> => {
    // Serialize concurrent connection attempts - only one connection should be created
    if (connectInFlightRef.current) {
      return await connectInFlightRef.current;
    }

    const connectPromise = (async (): Promise<HubConnection | null> => {
      const deviceId = authService.getDeviceId();
      if (!deviceId) {
        setError(t('prefill.errors.notAuthenticated'));
        return null;
      }

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
          .withUrl(`${SIGNALR_BASE}/prefill-daemon?deviceId=${encodeURIComponent(deviceId)}`)
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
          cachedAnimationQueueRef,
          isProcessingAnimationRef
        });

        await connection.start();
        hubConnection.current = connection;
        setIsConnecting(false);
        return connection;
      } catch (err) {
        console.error('Failed to connect to hub:', err);
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
    setSession,
    setTimeRemaining,
    setIsLoggedIn,
    setIsPrefillActive,
    setPrefillProgress,
    onSessionEnd,
    clearBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage,
    setBackgroundCompletionRef,
    sessionRef,
    isCancelling,
    currentAnimationAppIdRef,
    expectedAppCountRef,
    downloadedGamesCountRef,
    cachedGamesCountRef,
    totalBytesDownloadedRef,
    enqueueAnimation,
    resetAnimationState,
    cachedAnimationQueueRef,
    isProcessingAnimationRef,
    t
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
        await connection.invoke('SubscribeToSession', activeSession.id);

        setSession(activeSession);
        setTimeRemaining(activeSession.timeRemainingSeconds);
        setIsLoggedIn(activeSession.authState === 'Authenticated');

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

        if (activeSession.authState === 'Authenticated') {
          addLog('info', t('prefill.log.alreadyLoggedIn'));
        } else {
          addLog('info', t('prefill.log.loginToSteamPrompt'));
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
                const formattedDuration = formatDuration(lastResult.durationSeconds);
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
          // If lastResult is null or status is 'in_progress', the progress will be updated via SignalR events
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
      console.error('Failed to initialize session:', err);
    } finally {
      setIsInitializing(false);
    }
  }, [
    connectToHub,
    addLog,
    setBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage,
    t
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

        const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSession');
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
          addLog('info', t('prefill.log.alreadyLoggedIn'));
        } else {
          addLog(
            'success',
            t('prefill.log.sessionCreated'),
            t('prefill.log.containerDetail', { name: sessionDto.containerName })
          );
          addLog('info', t('prefill.log.loginToSteamBeforePrefill'));
        }
        addLog(
          'info',
          t('prefill.log.sessionExpiresIn', {
            time: formatTimeRemaining(sessionDto.timeRemainingSeconds)
          })
        );

        await connection.invoke('SubscribeToSession', sessionDto.id);
        setIsCreating(false);
      } catch (err) {
        console.error('Failed to create session:', err);
        const errorMessage =
          err instanceof Error ? err.message : t('prefill.errors.failedCreateSession');
        setError(errorMessage);
        addLog('error', errorMessage);
        setIsCreating(false);
      }
    },
    [connectToHub, addLog, t]
  );

  // Initialize on mount
  useEffect(() => {
    initializeSession();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return {
    // Connection
    hubConnection,
    isConnecting,
    connectToHub,

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
    isPrefillActive,

    // Session management
    initializeSession,
    createSession,

    // Error
    error,
    setError,

    // Refs
    isCancelling,
    expectedAppCountRef,
    sessionRef
  };
}
