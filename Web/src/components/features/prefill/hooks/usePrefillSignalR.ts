import { useRef, useState, useCallback, useEffect } from 'react';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { SIGNALR_BASE } from '@utils/constants';
import authService from '@services/auth.service';
import { formatDuration, type SteamAuthState, type PrefillSessionDto } from '../types';
import type { LogEntryType } from '../ActivityLog';

interface PrefillProgress {
  state: string;
  message?: string;
  currentAppId: number;
  currentAppName?: string;
  percentComplete: number;
  bytesDownloaded: number;
  totalBytes: number;
  bytesPerSecond: number;
  elapsedSeconds: number;
}

interface CachedAnimationItem {
  appId: number;
  appName?: string;
  totalBytes: number;
  progress: PrefillProgress & { totalApps?: number };
}

interface BackgroundCompletion {
  completedAt: string;
  message: string;
  duration: number;
}

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

  // Connection refs
  const hubConnection = useRef<HubConnection | null>(null);
  const initializationAttempted = useRef(false);
  const isCancelling = useRef(false);

  // Animation refs
  const isReceivingProgressRef = useRef(false);
  const cachedAnimationCountRef = useRef(0);
  const currentAnimationAppIdRef = useRef(0);
  const cachedAnimationQueueRef = useRef<CachedAnimationItem[]>([]);
  const isProcessingAnimationRef = useRef(false);

  // Completion tracking refs
  const expectedAppCountRef = useRef(0);
  const completedAppCountRef = useRef(0);
  const prefillDurationRef = useRef(0);
  const prefillStartTimeRef = useRef(0);
  const hasShownCompletionRef = useRef(false);
  const pendingCompletionRef = useRef<{ durationSeconds: number; completedApps: number } | null>(null);

  // Ref for setBackgroundCompletion (avoids stale closure)
  const setBackgroundCompletionRef = useRef(setBackgroundCompletion);

  // Session ref for SignalR handlers
  const sessionRef = useRef<PrefillSessionDto | null>(null);

  // State
  const [session, setSession] = useState<PrefillSessionDto | null>(null);
  const [isConnecting, setIsConnecting] = useState(false);
  const [isCreating, setIsCreating] = useState(false);
  const [isInitializing, setIsInitializing] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [timeRemaining, setTimeRemaining] = useState<number>(0);
  const [isLoggedIn, setIsLoggedIn] = useState(false);
  const [isPrefillActive, setIsPrefillActive] = useState(false);
  const [prefillProgress, setPrefillProgress] = useState<PrefillProgress | null>(null);

  // Keep refs in sync
  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  useEffect(() => {
    setBackgroundCompletionRef.current = setBackgroundCompletion;
  }, [setBackgroundCompletion]);

  const formatTimeRemaining = useCallback((seconds: number): string => {
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs = seconds % 60;
    if (hours > 0) {
      return `${hours}:${minutes.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    }
    return `${minutes}:${secs.toString().padStart(2, '0')}`;
  }, []);

  const connectToHub = useCallback(async (): Promise<HubConnection | null> => {
    const deviceId = authService.getDeviceId();
    if (!deviceId) {
      setError('Not authenticated');
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

      // Handle daemon output - parse and add to log
      connection.on('TerminalOutput', (_sessionId: string, output: string) => {
        const trimmed = output.trim();
        if (!trimmed) return;

        let type: LogEntryType = 'info';
        if (trimmed.includes('Error') || trimmed.includes('error') || trimmed.includes('failed')) {
          type = 'error';
        } else if (trimmed.includes('Success') || trimmed.includes('Complete') || trimmed.includes('Done')) {
          type = 'success';
        } else if (trimmed.includes('Warning') || trimmed.includes('warn')) {
          type = 'warning';
        } else if (trimmed.includes('Download') || trimmed.includes('Prefill') || trimmed.includes('%')) {
          type = 'download';
        }
        addLog(type, trimmed);
      });

      // Handle auth state changes from backend
      connection.on('AuthStateChanged', (_sessionId: string, newState: SteamAuthState) => {
        onAuthStateChanged(newState);
      });

      // Handle session subscribed confirmation
      connection.on('SessionSubscribed', (sessionDto: PrefillSessionDto) => {
        setSession(sessionDto);
        setTimeRemaining(sessionDto.timeRemainingSeconds);
        setIsLoggedIn(sessionDto.authState === 'Authenticated');
      });

      // Handle session ended
      connection.on('SessionEnded', (_sessionId: string, reason: string) => {
        addLog('warning', `Session ended: ${reason}`);
        setSession(null);
        setIsLoggedIn(false);
        setIsPrefillActive(false);
        setPrefillProgress(null);
        // Clear all prefill-related storage when session ends
        clearAllPrefillStorage();
        onSessionEnd?.();
      });

      // Handle prefill progress updates
      connection.on('PrefillProgress', (_sessionId: string, progress: PrefillProgress & { totalApps: number }) => {
        const isFinalState = progress.state === 'completed' || progress.state === 'failed' ||
                           progress.state === 'cancelled' || progress.state === 'idle';

        if (isFinalState) {
          isCancelling.current = false;
          isReceivingProgressRef.current = false;
          setPrefillProgress(null);
          return;
        }

        if (isCancelling.current) return;
        isReceivingProgressRef.current = true;

        if (progress.state === 'downloading') {
          if (currentAnimationAppIdRef.current === 0 || currentAnimationAppIdRef.current === progress.currentAppId) {
            setPrefillProgress(progress);
          }
        } else if (progress.state === 'app_completed') {
          currentAnimationAppIdRef.current = 0;
          setPrefillProgress(prev => prev ? {
            ...prev,
            state: 'app_completed',
            percentComplete: 100,
            currentAppName: progress.currentAppName || prev.currentAppName
          } : null);
        } else if (progress.state === 'already_cached') {
          if (expectedAppCountRef.current === 0 && progress.totalApps > 0) {
            expectedAppCountRef.current = progress.totalApps;
          }
          if (progress.elapsedSeconds > 0) {
            prefillDurationRef.current = progress.elapsedSeconds;
          }

          cachedAnimationCountRef.current++;
          completedAppCountRef.current++;
          cachedAnimationQueueRef.current.push({
            appId: progress.currentAppId,
            appName: progress.currentAppName,
            totalBytes: progress.totalBytes || 0,
            progress
          });

          const processAnimationQueue = () => {
            if (isProcessingAnimationRef.current || cachedAnimationQueueRef.current.length === 0) return;

            const item = cachedAnimationQueueRef.current.shift();
            if (!item) return;

            isProcessingAnimationRef.current = true;
            currentAnimationAppIdRef.current = item.appId;

            const animationDuration = 2000;
            const startTime = Date.now();

            const animateProgress = () => {
              const elapsed = Date.now() - startTime;
              const percent = Math.min(100, (elapsed / animationDuration) * 100);

              setPrefillProgress({
                state: 'already_cached',
                currentAppId: item.appId,
                currentAppName: item.appName,
                percentComplete: percent,
                bytesDownloaded: Math.floor((percent / 100) * item.totalBytes),
                totalBytes: item.totalBytes,
                bytesPerSecond: 0,
                elapsedSeconds: 0
              });

              if (elapsed < animationDuration) {
                requestAnimationFrame(animateProgress);
              } else {
                setTimeout(() => {
                  cachedAnimationCountRef.current--;
                  isProcessingAnimationRef.current = false;
                  currentAnimationAppIdRef.current = 0;
                  // If queue is empty, clear progress (prefill may have already completed)
                  if (cachedAnimationQueueRef.current.length === 0) {
                    setPrefillProgress(null);
                  } else {
                    processAnimationQueue();
                  }
                }, 100);
              }
            };

            animateProgress();
          };

          processAnimationQueue();
        } else if (['loading-metadata', 'metadata-loaded', 'starting', 'preparing'].includes(progress.state)) {
          if (progress.message) {
            addLog('info', progress.message);
          }
          if (progress.message?.includes('0 games')) {
            setPrefillProgress(null);
            return;
          }
          setPrefillProgress({ ...progress, percentComplete: 0, bytesDownloaded: 0, totalBytes: 0 });
        } else if (['completed', 'failed', 'cancelled'].includes(progress.state)) {
          setPrefillProgress(null);
        }
      });

      // Handle status changes
      connection.on('StatusChanged', (_sessionId: string, status: { status: string; message: string }) => {
        if (status.message) {
          addLog('info', `Status: ${status.message}`);
        }
      });

      // Handle prefill state changes
      connection.on('PrefillStateChanged', (_sessionId: string, state: string, durationSeconds?: number) => {
        const resetAnimationState = () => {
          cachedAnimationQueueRef.current = [];
          isProcessingAnimationRef.current = false;
          currentAnimationAppIdRef.current = 0;
          cachedAnimationCountRef.current = 0;
        };

        if (state === 'started') {
          setIsPrefillActive(true);
          addLog('download', 'Prefill operation started');
          prefillDurationRef.current = 0;
          prefillStartTimeRef.current = Date.now();
          isReceivingProgressRef.current = true;
          cachedAnimationCountRef.current = 0;
          pendingCompletionRef.current = null;
          completedAppCountRef.current = 0;
          hasShownCompletionRef.current = false;
          resetAnimationState();
          clearBackgroundCompletion();
          try {
            sessionStorage.setItem('prefill_in_progress', JSON.stringify({
              startedAt: new Date().toISOString(),
              sessionId: _sessionId
            }));
          } catch { /* ignore */ }
        } else if (state === 'completed') {
          setIsPrefillActive(false);
          const duration = durationSeconds ?? 0;
          const formattedDuration = formatDuration(duration);
          addLog('success', `Prefill completed in ${formattedDuration}`);
          isCancelling.current = false;
          isReceivingProgressRef.current = false;

          // If there are pending cached animations, let them finish before clearing
          const hasPendingAnimations = cachedAnimationQueueRef.current.length > 0 || isProcessingAnimationRef.current;
          if (!hasPendingAnimations) {
            setPrefillProgress(null);
            resetAnimationState();
          }
          // Note: Animation completion handler will clear progress when done

          setBackgroundCompletionRef.current({
            completedAt: new Date().toISOString(),
            message: `Prefill completed in ${formattedDuration}`,
            duration: duration
          });
          try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
        } else if (state === 'failed') {
          setIsPrefillActive(false);
          addLog('error', 'Prefill operation failed');
          isCancelling.current = false;
          isReceivingProgressRef.current = false;
          setPrefillProgress(null);
          resetAnimationState();
          try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
        } else if (state === 'cancelled') {
          setIsPrefillActive(false);
          addLog('info', 'Prefill operation cancelled');
          isCancelling.current = false;
          isReceivingProgressRef.current = false;
          setPrefillProgress(null);
          resetAnimationState();
          try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
        }
      });

      // Handle daemon session updates
      connection.on('DaemonSessionCreated', (sessionDto: PrefillSessionDto) => {
        setSession((currentSession) => {
          if (currentSession && sessionDto.id === currentSession.id) {
            setTimeRemaining(sessionDto.timeRemainingSeconds);
            return sessionDto;
          }
          return currentSession;
        });
      });

      connection.on('DaemonSessionUpdated', (sessionDto: PrefillSessionDto) => {
        setSession((currentSession) => {
          if (currentSession && sessionDto.id === currentSession.id) {
            setTimeRemaining(sessionDto.timeRemainingSeconds);
            return sessionDto;
          }
          return currentSession;
        });
      });

      connection.on('PrefillHistoryUpdated', () => {
        // Admin pages use this - PrefillPanel doesn't need it
      });

      connection.onclose(() => {
        setIsConnecting(false);
      });

      connection.onreconnecting(() => {
        addLog('warning', 'Connection lost, reconnecting...');
      });

      connection.onreconnected(async () => {
        addLog('success', 'Reconnected to server');
        const currentSession = sessionRef.current;
        if (currentSession) {
          try {
            await connection.invoke('SubscribeToSession', currentSession.id);
            isReceivingProgressRef.current = false;

            const lastResult = await connection.invoke('GetLastPrefillResult', currentSession.id) as {
              status: string;
              completedAt: string;
              durationSeconds: number;
            } | null;

            if (lastResult && lastResult.status === 'completed') {
              const completedTime = new Date(lastResult.completedAt).getTime();
              if (Date.now() - completedTime < 5 * 60 * 1000 && !isCompletionDismissed(lastResult.completedAt)) {
                const formattedDuration = formatDuration(lastResult.durationSeconds);
                setBackgroundCompletion({
                  completedAt: lastResult.completedAt,
                  message: `Prefill completed in ${formattedDuration}`,
                  duration: lastResult.durationSeconds
                });
                addLog('success', `Prefill completed while disconnected (${formattedDuration})`);
              }
            }
            try { sessionStorage.removeItem('prefill_in_progress'); } catch { /* ignore */ }
          } catch (err) {
            console.error('Failed to resubscribe:', err);
          }
        }
      });

      await connection.start();
      hubConnection.current = connection;
      setIsConnecting(false);
      return connection;
    } catch (err) {
      console.error('Failed to connect to hub:', err);
      setError('Failed to connect to server');
      setIsConnecting(false);
      return null;
    }
  }, [addLog, onAuthStateChanged, onSessionEnd, clearBackgroundCompletion, setBackgroundCompletion, isCompletionDismissed, clearAllPrefillStorage]);

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
        addLog('info', 'Reconnecting to existing session...', `Session: ${activeSession.id}`);
        await connection.invoke('SubscribeToSession', activeSession.id);

        setSession(activeSession);
        setTimeRemaining(activeSession.timeRemainingSeconds);
        setIsLoggedIn(activeSession.authState === 'Authenticated');

        addLog('success', 'Reconnected to existing session', `Container: ${activeSession.containerName}`);
        addLog('info', `Session expires in ${formatTimeRemaining(activeSession.timeRemainingSeconds)}`);

        if (activeSession.authState === 'Authenticated') {
          addLog('info', 'Already logged in to Steam');
        } else {
          addLog('info', 'Click "Login to Steam" to authenticate');
        }

        // Check for missed completions
        try {
          isReceivingProgressRef.current = false;
          const lastResult = await connection.invoke('GetLastPrefillResult', activeSession.id) as {
            status: string;
            completedAt: string;
            durationSeconds: number;
          } | null;

          if (lastResult && lastResult.status === 'completed') {
            const completedTime = new Date(lastResult.completedAt).getTime();
            if (Date.now() - completedTime < 5 * 60 * 1000) {
              const currentBgCompletion = sessionStorage.getItem('prefill_background_completion');
              if (!currentBgCompletion && !isCompletionDismissed(lastResult.completedAt)) {
                const formattedDuration = formatDuration(lastResult.durationSeconds);
                setBackgroundCompletion({
                  completedAt: lastResult.completedAt,
                  message: `Prefill completed in ${formattedDuration}`,
                  duration: lastResult.durationSeconds
                });
                addLog('success', `Prefill completed while away (${formattedDuration})`);
              }
            }
          }
        } catch {
          // Non-critical
        }
      } else {
        // No active session found - check if there's stale storage data from a previous session
        // This happens when the server was stopped/restarted and cleared sessions
        const hasStaleData = sessionStorage.getItem('prefill_session_id') ||
                           sessionStorage.getItem('prefill_activity_log') ||
                           sessionStorage.getItem('prefill_in_progress');
        if (hasStaleData) {
          console.log('[usePrefillSignalR] No active session but found stale storage data, clearing...');
          clearAllPrefillStorage();
        }
      }
    } catch (err) {
      console.error('Failed to initialize session:', err);
    } finally {
      setIsInitializing(false);
    }
  }, [connectToHub, addLog, formatTimeRemaining, setBackgroundCompletion, isCompletionDismissed, clearAllPrefillStorage]);

  const createSession = useCallback(async (clearLogs: () => void) => {
    setIsCreating(true);
    setError(null);
    clearLogs();

    try {
      let connection = hubConnection.current;
      if (!connection || connection.state !== 'Connected') {
        connection = await connectToHub();
      }

      if (!connection) {
        throw new Error('Failed to establish connection');
      }

      addLog('info', 'Creating prefill session...');

      const sessionDto = await connection.invoke<PrefillSessionDto>('CreateSession');
      setSession(sessionDto);
      setTimeRemaining(sessionDto.timeRemainingSeconds);

      const isExistingSession = sessionDto.authState === 'Authenticated';
      setIsLoggedIn(isExistingSession);

      if (isExistingSession) {
        addLog('success', 'Connected to existing session', `Container: ${sessionDto.containerName}`);
        addLog('info', 'Already logged in to Steam');
      } else {
        addLog('success', 'Session created successfully', `Container: ${sessionDto.containerName}`);
        addLog('info', 'Click "Login to Steam" to authenticate before using prefill commands');
      }
      addLog('info', `Session expires in ${formatTimeRemaining(sessionDto.timeRemainingSeconds)}`);

      await connection.invoke('SubscribeToSession', sessionDto.id);
      setIsCreating(false);
    } catch (err) {
      console.error('Failed to create session:', err);
      const errorMessage = err instanceof Error ? err.message : 'Failed to create session';
      setError(errorMessage);
      addLog('error', errorMessage);
      setIsCreating(false);
    }
  }, [connectToHub, formatTimeRemaining, addLog]);

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
    isInitializing: isInitializing || isCreating,

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
