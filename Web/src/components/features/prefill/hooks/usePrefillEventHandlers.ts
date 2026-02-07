import type { HubConnection } from '@microsoft/signalr';
import { formatBytes } from '@utils/formatters';
import {
  formatDuration,
  type SteamAuthState,
  type PrefillSessionDto
} from '../types';
import type { LogEntryType } from '../ActivityLog';
import i18n from '../../../../i18n';
import { COMPLETION_NOTIFICATION_WINDOW_MS } from './prefillConstants';
import type { PrefillProgress, BackgroundCompletion, CachedAnimationItem } from './prefillTypes';

interface UsePrefillEventHandlersOptions {
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  onAuthStateChanged: (state: SteamAuthState) => void;
  setSession: React.Dispatch<React.SetStateAction<PrefillSessionDto | null>>;
  setTimeRemaining: React.Dispatch<React.SetStateAction<number>>;
  setIsLoggedIn: React.Dispatch<React.SetStateAction<boolean>>;
  setIsPrefillActive: React.Dispatch<React.SetStateAction<boolean>>;
  setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>;
  onSessionEnd?: () => void;
  clearAllPrefillStorage: () => void;
  setBackgroundCompletionRef: React.RefObject<(completion: BackgroundCompletion) => void>;
  clearBackgroundCompletion: () => void;
  isCompletionDismissed: (completedAt: string) => boolean;
  sessionRef: React.RefObject<PrefillSessionDto | null>;
  isCancelling: React.RefObject<boolean>;
  currentAnimationAppIdRef: React.RefObject<number>;
  expectedAppCountRef: React.RefObject<number>;
  downloadedGamesCountRef: React.RefObject<number>;
  cachedGamesCountRef: React.RefObject<number>;
  totalBytesDownloadedRef: React.RefObject<number>;
  enqueueAnimation: (item: CachedAnimationItem, setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>) => void;
  resetAnimationState: () => void;
  cachedAnimationQueueRef: React.RefObject<CachedAnimationItem[]>;
  isProcessingAnimationRef: React.RefObject<boolean>;
}

/**
 * Registers SignalR event handlers for the prefill feature.
 * This is a plain function (not a hook) so it can be called from callbacks.
 */
export function registerPrefillEventHandlers(
  connection: HubConnection,
  options: UsePrefillEventHandlersOptions
): void {
  const {
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
  } = options;

  const t = i18n.t.bind(i18n);

  // Handle daemon output - parse and add to log
    connection.on('TerminalOutput', (_sessionId: string, output: string) => {
      const trimmed = output.trim();
      if (!trimmed) return;

      let type: LogEntryType = 'info';
      if (
        trimmed.includes('Error') ||
        trimmed.includes('error') ||
        trimmed.includes('failed')
      ) {
        type = 'error';
      } else if (
        trimmed.includes('Success') ||
        trimmed.includes('Complete') ||
        trimmed.includes('Done')
      ) {
        type = 'success';
      } else if (trimmed.includes('Warning') || trimmed.includes('warn')) {
        type = 'warning';
      } else if (
        trimmed.includes('Download') ||
        trimmed.includes('Prefill') ||
        trimmed.includes('%')
      ) {
        type = 'download';
      }
      addLog(type, trimmed);
    });

    // Handle auth state changes from backend
    connection.on('AuthStateChanged', ({ authState }: { sessionId: string; authState: SteamAuthState }) => {
      onAuthStateChanged(authState);
    });

    // Handle session subscribed confirmation
    connection.on('SessionSubscribed', (sessionDto: PrefillSessionDto) => {
      setSession(sessionDto);
      setTimeRemaining(sessionDto.timeRemainingSeconds);
      setIsLoggedIn(sessionDto.authState === 'Authenticated');
    });

    // Handle session ended (sent to session owner)
    connection.on('SessionEnded', ({ reason }: { sessionId: string; reason: string }) => {
      addLog('warning', t('prefill.log.sessionEnded', { reason: reason }));
      setSession(null);
      setIsLoggedIn(false);
      setIsPrefillActive(false);
      setPrefillProgress(null);
      // Clear all prefill-related storage when session ends
      clearAllPrefillStorage();
      onSessionEnd?.();
    });

    // Handle daemon session terminated (broadcast to all clients)
    // This is used by admin pages; for the prefill panel, SessionEnded handles our session
    connection.on(
      'DaemonSessionTerminated',
      ({ sessionId: terminatedSessionId, reason }: { sessionId: string; reason: string }) => {
        // Check if this termination is for our current session
        const currentSession = sessionRef.current;
        if (currentSession && terminatedSessionId === currentSession.id) {
          // Our session was terminated externally (e.g., by admin)
          addLog('warning', t('prefill.log.sessionTerminated', { reason: reason }));
          setSession(null);
          setIsLoggedIn(false);
          setIsPrefillActive(false);
          setPrefillProgress(null);
          clearAllPrefillStorage();
          onSessionEnd?.();
        }
      }
    );

    // Handle prefill progress updates
    connection.on(
      'PrefillProgress',
      ({ progress }: { sessionId: string; progress: PrefillProgress & { totalApps: number } }) => {
        const isFinalState =
          progress.state === 'completed' ||
          progress.state === 'failed' ||
          progress.state === 'cancelled' ||
          progress.state === 'idle';

        if (isFinalState) {
          isCancelling.current = false;
          setPrefillProgress(null);
          return;
        }

        if (isCancelling.current) return;

        if (progress.state === 'downloading') {
          if (
            currentAnimationAppIdRef.current === 0 ||
            currentAnimationAppIdRef.current === progress.currentAppId
          ) {
            setPrefillProgress(progress);
          }
        } else if (progress.state === 'app_completed') {
          currentAnimationAppIdRef.current = 0;

          // Log game completion with download size
          const gameName = progress.currentAppName || `App ${progress.currentAppId}`;
          const sizeDownloaded = formatBytes(progress.bytesDownloaded || progress.totalBytes || 0);
          addLog('success', `${gameName} - Downloaded ${sizeDownloaded}`);

          // Track for summary
          downloadedGamesCountRef.current++;
          totalBytesDownloadedRef.current += progress.bytesDownloaded || progress.totalBytes || 0;

          setPrefillProgress((prev) =>
            prev
              ? {
                  ...prev,
                  state: 'app_completed',
                  percentComplete: 100,
                  currentAppName: progress.currentAppName || prev.currentAppName
                }
              : null
          );
        } else if (progress.state === 'already_cached') {
          // Log cached game
          const gameName = progress.currentAppName || `App ${progress.currentAppId}`;
          addLog('info', `${gameName} - Already up to date`);

          // Track for summary
          cachedGamesCountRef.current++;

          if (expectedAppCountRef.current === 0 && progress.totalApps > 0) {
            expectedAppCountRef.current = progress.totalApps;
          }

          enqueueAnimation({
            appId: progress.currentAppId,
            appName: progress.currentAppName,
            totalBytes: progress.totalBytes || 0
          }, setPrefillProgress);
        } else if (
          ['loading-metadata', 'metadata-loaded', 'starting', 'preparing'].includes(
            progress.state
          )
        ) {
          if (progress.message) {
            addLog('info', progress.message);
          }
          if (progress.message?.includes('0 games')) {
            setPrefillProgress(null);
            return;
          }
          setPrefillProgress({
            ...progress,
            percentComplete: 0,
            bytesDownloaded: 0,
            totalBytes: 0
          });
        } else if (['completed', 'failed', 'cancelled'].includes(progress.state)) {
          setPrefillProgress(null);
        }
      }
    );

    // Handle status changes
    connection.on(
      'StatusChanged',
      ({ status }: { sessionId: string; status: { status: string; message: string } }) => {
        if (status.message) {
          addLog('info', t('prefill.log.statusMessage', { message: status.message }));
        }
      }
    );

    // Handle prefill state changes
    connection.on(
      'PrefillStateChanged',
      ({ sessionId: stateSessionId, state, durationSeconds }: { sessionId: string; state: string; durationSeconds?: number }) => {
        if (state === 'started') {
          setIsPrefillActive(true);
          addLog('download', t('prefill.log.prefillStarted'));
          resetAnimationState();
          clearBackgroundCompletion();

          // Reset counters for new prefill
          downloadedGamesCountRef.current = 0;
          cachedGamesCountRef.current = 0;
          totalBytesDownloadedRef.current = 0;
          try {
            sessionStorage.setItem(
              'prefill_in_progress',
              JSON.stringify({
                startedAt: new Date().toISOString(),
                sessionId: stateSessionId
              })
            );
          } catch {
            /* ignore */
          }
        } else if (state === 'completed') {
          setIsPrefillActive(false);
          const duration = durationSeconds ?? 0;
          const formattedDuration = formatDuration(duration);
          addLog('success', t('prefill.log.prefillCompleted', { duration: formattedDuration }));

          // Log summary of what was prefilled
          const downloaded = downloadedGamesCountRef.current;
          const cached = cachedGamesCountRef.current;
          const totalBytes = totalBytesDownloadedRef.current;
          if (downloaded > 0 || cached > 0) {
            const parts: string[] = [];
            if (downloaded > 0) {
              parts.push(`${downloaded} game${downloaded === 1 ? '' : 's'} downloaded (${formatBytes(totalBytes)})`);
            }
            if (cached > 0) {
              parts.push(`${cached} game${cached === 1 ? '' : 's'} already up to date`);
            }
            addLog('info', `Summary: ${parts.join(', ')}`);
          }

          isCancelling.current = false;

          // If there are pending cached animations, let them finish before clearing
          const hasPendingAnimations =
            cachedAnimationQueueRef.current.length > 0 || isProcessingAnimationRef.current;
          if (!hasPendingAnimations) {
            setPrefillProgress(null);
            resetAnimationState();
          }
          // Note: Animation completion handler will clear progress when done

          setBackgroundCompletionRef.current({
            completedAt: new Date().toISOString(),
            message: t('prefill.completion.message', { duration: formattedDuration }),
            duration: duration
          });
          try {
            sessionStorage.removeItem('prefill_in_progress');
          } catch {
            /* ignore */
          }
        } else if (state === 'failed') {
          setIsPrefillActive(false);
          addLog('error', t('prefill.log.prefillFailed'));
          isCancelling.current = false;

          setPrefillProgress(null);
          resetAnimationState();
          try {
            sessionStorage.removeItem('prefill_in_progress');
          } catch {
            /* ignore */
          }
        } else if (state === 'cancelled') {
          setIsPrefillActive(false);
          addLog('info', t('prefill.log.prefillCancelled'));
          isCancelling.current = false;

          setPrefillProgress(null);
          resetAnimationState();
          try {
            sessionStorage.removeItem('prefill_in_progress');
          } catch {
            /* ignore */
          }
        }
      }
    );

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

    // Register no-op handler for CredentialChallenge to prevent SignalR warning
    // Actual handling is done in usePrefillSteamAuth when auth UI is active
    connection.on('CredentialChallenge', () => {
      // Handled by usePrefillSteamAuth
    });

    connection.onclose(() => {
      // Connection closed - state managed by main hook
    });

    connection.onreconnecting(() => {
      addLog('warning', t('prefill.log.connectionLostReconnecting'));
    });

    connection.onreconnected(async () => {
      addLog('success', t('prefill.log.reconnected'));
      const currentSession = sessionRef.current;
      if (currentSession) {
        try {
          await connection.invoke('SubscribeToSession', currentSession.id);

          const lastResult = (await connection.invoke(
            'GetLastPrefillResult',
            currentSession.id
          )) as {
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
              const formattedDuration = formatDuration(lastResult.durationSeconds);
              setBackgroundCompletionRef.current({
                completedAt: lastResult.completedAt,
                message: t('prefill.completion.message', { duration: formattedDuration }),
                duration: lastResult.durationSeconds
              });
              addLog(
                'success',
                t('prefill.log.prefillCompletedWhileDisconnected', {
                  duration: formattedDuration
                })
              );
            }
          }
          try {
            sessionStorage.removeItem('prefill_in_progress');
          } catch {
            /* ignore */
          }
        } catch (err) {
          console.error('Failed to resubscribe:', err);
        }
      }
    });
}
