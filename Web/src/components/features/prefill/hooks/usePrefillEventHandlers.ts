import type { HubConnection } from '@microsoft/signalr';
import { formatBytes } from '@utils/formatters';
import { formatDurationFromSeconds, type PrefillSessionDto } from '../types';
import type { DaemonAuthState } from '@/types/operations';
import type { LogEntryType } from '../ActivityLog.utils';
import i18n from '../../../../i18n';
import { COMPLETION_NOTIFICATION_WINDOW_MS, getEventName } from './prefillConstants';
import type { PrefillProgress, BackgroundCompletion, CachedAnimationItem } from './prefillTypes';

interface UsePrefillEventHandlersOptions {
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  onAuthStateChanged: (state: DaemonAuthState) => void;
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
  currentAnimationAppIdRef: React.RefObject<string>;
  expectedAppCountRef: React.RefObject<number>;
  downloadedGamesCountRef: React.RefObject<number>;
  cachedGamesCountRef: React.RefObject<number>;
  totalBytesDownloadedRef: React.RefObject<number>;
  enqueueAnimation: (
    item: CachedAnimationItem,
    setPrefillProgress: React.Dispatch<React.SetStateAction<PrefillProgress | null>>
  ) => void;
  resetAnimationState: () => void;
  stopAnimations: () => void;
  cachedAnimationQueueRef: React.RefObject<CachedAnimationItem[]>;
  isProcessingAnimationRef: React.RefObject<boolean>;
  serviceId: string;
  /** CONTRACT: invokes GetCurrentPrefillProgress(sessionId) and binds the live bar. */
  rehydratePrefillProgress: (connection: HubConnection, sessionId: string) => Promise<void>;
  /** Builds the coarse 'reconnecting' placeholder from a session DTO that is mid-prefill. */
  seedReconnectingProgressFromSession: (sessionDto: PrefillSessionDto) => PrefillProgress;
  /** Reactive mirror of isCancelling for the Cancel button's disabled "Cancelling..." state. */
  setIsCancellingState: React.Dispatch<React.SetStateAction<boolean>>;
  /** Watchdog timer started on Cancel; terminal events clear it. */
  cancelWatchdogRef: React.RefObject<ReturnType<typeof setTimeout> | null>;
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
    stopAnimations,
    cachedAnimationQueueRef,
    isProcessingAnimationRef,
    serviceId,
    rehydratePrefillProgress,
    seedReconnectingProgressFromSession,
    setIsCancellingState,
    cancelWatchdogRef
  } = options;

  // Clears the cancel watchdog + reactive "Cancelling..." state. Called from every terminal path
  // so the Cancel button never gets stuck disabled and the watchdog can't fire after the fact.
  const clearCancelTracking = (): void => {
    setIsCancellingState(false);
    if (cancelWatchdogRef.current !== null) {
      clearTimeout(cancelWatchdogRef.current);
      cancelWatchdogRef.current = null;
    }
  };

  const t = i18n.t.bind(i18n);

  // Handle daemon output - parse and add to log
  connection.on(getEventName('TerminalOutput', serviceId), (_sessionId: string, output: string) => {
    const trimmed = output.trim();
    if (!trimmed) return;

    let type: LogEntryType = 'info';
    if (trimmed.includes('Error') || trimmed.includes('error') || trimmed.includes('failed')) {
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
  connection.on(
    getEventName('AuthStateChanged', serviceId),
    ({ authState }: { sessionId: string; authState: DaemonAuthState }) => {
      onAuthStateChanged(authState);
    }
  );

  // Handle session subscribed confirmation
  connection.on(getEventName('SessionSubscribed', serviceId), (sessionDto: PrefillSessionDto) => {
    setSession(sessionDto);
    setTimeRemaining(sessionDto.timeRemainingSeconds);
    setIsLoggedIn(sessionDto.authState === 'Authenticated');

    // Server truth: a prefill is already running for this session. Seed the bar immediately
    // (coarse 'reconnecting' placeholder). V6: do NOT invoke GetCurrentPrefillProgress here — the
    // backend already replays the existing PrefillProgress event to Clients.Caller on subscribe, so
    // the real bar binds from that push. Adding the invoke too is a redundant, racy double-write
    // (two snapshots + the live tick) that can flicker the bar backward. The visibilitychange /
    // onreconnected paths keep the invoke because there is no fresh subscribe replay there.
    if (sessionDto.isPrefilling) {
      setIsPrefillActive(true);
      setPrefillProgress((prev) => prev ?? seedReconnectingProgressFromSession(sessionDto));
    }
  });

  // Handle session ended (sent to session owner)
  connection.on(
    getEventName('SessionEnded', serviceId),
    ({ reason }: { sessionId: string; reason: string }) => {
      addLog('warning', t('prefill.log.sessionEnded', { reason: reason }));
      setSession(null);
      setIsLoggedIn(false);
      setIsPrefillActive(false);
      clearCancelTracking();
      stopAnimations();
      setPrefillProgress(null);
      // Clear all prefill-related storage when session ends
      clearAllPrefillStorage();
      onSessionEnd?.();
    }
  );

  // Handle daemon session terminated (broadcast to all clients)
  // This is used by admin pages; for the prefill panel, SessionEnded handles our session
  connection.on(
    getEventName('DaemonSessionTerminated', serviceId),
    ({ sessionId: terminatedSessionId, reason }: { sessionId: string; reason: string }) => {
      // Check if this termination is for our current session
      const currentSession = sessionRef.current;
      if (currentSession && terminatedSessionId === currentSession.id) {
        // Our session was terminated externally (e.g., by admin)
        addLog('warning', t('prefill.log.sessionTerminated', { reason: reason }));
        setSession(null);
        setIsLoggedIn(false);
        setIsPrefillActive(false);
        clearCancelTracking();
        stopAnimations();
        setPrefillProgress(null);
        clearAllPrefillStorage();
        onSessionEnd?.();
      }
    }
  );

  // Handle prefill progress updates
  connection.on(
    getEventName('PrefillProgress', serviceId),
    ({ progress }: { sessionId: string; progress: PrefillProgress & { totalApps: number } }) => {
      const isFinalState =
        progress.state === 'completed' ||
        progress.state === 'failed' ||
        progress.state === 'cancelled' ||
        progress.state === 'idle';

      if (isFinalState) {
        isCancelling.current = false;
        clearCancelTracking();
        stopAnimations();
        setPrefillProgress(null);
        return;
      }

      if (isCancelling.current) return;

      // Seed the expected-app count from the daemon's totalApps the first time we learn it, so
      // the two-tier "Game X of N" overall bar can render even for prefill-all/recent jobs where
      // the count is not known up-front on the client.
      if (
        (expectedAppCountRef.current ?? 0) === 0 &&
        progress.totalApps &&
        progress.totalApps > 0
      ) {
        expectedAppCountRef.current = progress.totalApps;
      }

      if (progress.state === 'downloading') {
        if (
          !currentAnimationAppIdRef.current ||
          currentAnimationAppIdRef.current === progress.currentAppId
        ) {
          setPrefillProgress({
            ...progress,
            expectedAppCount: expectedAppCountRef.current || progress.totalApps || undefined,
            // V11: plumb the client-tracked running counts so "Game X of N" + the overall bar
            // advance live. The daemon only sends real counters in the FINAL completed summary,
            // so without this processedApps stays 0 and the position freezes at "Game 1 of N".
            updatedApps: downloadedGamesCountRef.current,
            alreadyUpToDate: cachedGamesCountRef.current
          });
        }
      } else if (progress.state === 'app_completed') {
        currentAnimationAppIdRef.current = '';

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
                currentAppName: progress.currentAppName || prev.currentAppName,
                // V11: the count was just incremented above, so processedApps now includes this
                // finished app and "Game X of N" advances instead of sticking at 1.
                updatedApps: downloadedGamesCountRef.current,
                alreadyUpToDate: cachedGamesCountRef.current
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

        enqueueAnimation(
          {
            appId: progress.currentAppId,
            appName: progress.currentAppName,
            totalBytes: progress.totalBytes || 0,
            // V11: snapshot the running counts (the cached count was just incremented above) so the
            // animation's fresh PrefillProgress keeps "Game X of N" + the overall bar advancing.
            expectedAppCount: expectedAppCountRef.current || progress.totalApps || undefined,
            updatedApps: downloadedGamesCountRef.current,
            alreadyUpToDate: cachedGamesCountRef.current
          },
          setPrefillProgress
        );
      } else if (
        ['loading-metadata', 'metadata-loaded', 'starting', 'preparing'].includes(progress.state)
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
    getEventName('StatusChanged', serviceId),
    ({ status }: { sessionId: string; status: { status: string; message: string } }) => {
      if (status.message) {
        addLog('info', t('prefill.log.statusMessage', { message: status.message }));
      }
    }
  );

  // Handle prefill state changes
  connection.on(
    getEventName('PrefillStateChanged', serviceId),
    ({
      sessionId: stateSessionId,
      state,
      durationSeconds
    }: {
      sessionId: string;
      state: string;
      durationSeconds?: number;
    }) => {
      if (state === 'started') {
        setIsPrefillActive(true);
        setIsCancellingState(false);
        addLog('download', t('prefill.log.prefillStarted'));
        resetAnimationState();
        clearBackgroundCompletion();

        // Reset counters for new prefill
        downloadedGamesCountRef.current = 0;
        cachedGamesCountRef.current = 0;
        totalBytesDownloadedRef.current = 0;
        // V5: also clear the expected-app count so a stale total from a prior run (e.g. a
        // prefill-top(50)) can't leak into a new prefill-recent(12) job on a non-executeCommand
        // start path (2nd tab / replayed start). The PrefillProgress seed-guard re-derives the
        // correct count from the daemon's totalApps on the first tick.
        expectedAppCountRef.current = 0;
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
        const formattedDuration = formatDurationFromSeconds(duration);
        addLog('success', t('prefill.log.prefillCompleted', { duration: formattedDuration }));

        // Log summary of what was prefilled
        const downloaded = downloadedGamesCountRef.current;
        const cached = cachedGamesCountRef.current;
        const totalBytes = totalBytesDownloadedRef.current;
        if (downloaded > 0 || cached > 0) {
          const parts: string[] = [];
          if (downloaded > 0) {
            parts.push(
              `${downloaded} game${downloaded === 1 ? '' : 's'} downloaded (${formatBytes(totalBytes)})`
            );
          }
          if (cached > 0) {
            parts.push(`${cached} game${cached === 1 ? '' : 's'} already up to date`);
          }
          addLog('info', `Summary: ${parts.join(', ')}`);
        }

        isCancelling.current = false;
        clearCancelTracking();

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
        clearCancelTracking();

        stopAnimations();
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
        clearCancelTracking();

        stopAnimations();
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
  connection.on(
    getEventName('DaemonSessionCreated', serviceId),
    (sessionDto: PrefillSessionDto) => {
      setSession((currentSession) => {
        if (currentSession && sessionDto.id === currentSession.id) {
          setTimeRemaining(sessionDto.timeRemainingSeconds);
          return sessionDto;
        }
        return currentSession;
      });
    }
  );

  connection.on(
    getEventName('DaemonSessionUpdated', serviceId),
    (sessionDto: PrefillSessionDto) => {
      setSession((currentSession) => {
        if (currentSession && sessionDto.id === currentSession.id) {
          setTimeRemaining(sessionDto.timeRemainingSeconds);
          return sessionDto;
        }
        return currentSession;
      });
    }
  );

  connection.onreconnecting(() => {
    addLog('warning', t('prefill.log.connectionLostReconnecting'));
  });

  connection.onreconnected(async () => {
    addLog('success', t('prefill.log.reconnected'));
    const currentSession = sessionRef.current;
    if (currentSession) {
      try {
        await connection.invoke('SubscribeToSessionAsync', currentSession.id);

        // Re-bind the live progress bar from server truth (the daemon may still be prefilling
        // after the socket drop). CONTRACT: GetCurrentPrefillProgress.
        await rehydratePrefillProgress(connection, currentSession.id);

        const lastResult = (await connection.invoke('GetLastPrefillResult', currentSession.id)) as {
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
      } catch {
        // Failed to resubscribe after reconnection
      }
    }
  });
}
