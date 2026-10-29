import type { HubConnection } from '@microsoft/signalr';
import type { PrefillSessionDto } from '../types';
import type { DaemonAuthState } from '@/types/operations';
import type { GameServiceId } from '@/types/gameService';
import type { LogEntryType } from '../ActivityLog.utils';
import i18n from '@/i18n';
import { getEventName } from './prefillConstants';
import {
  getPrefillRunReasonKey,
  supportsConcurrentPrefill,
  type PrefillRun,
  type PrefillProgress
} from './prefillTypes';

interface UsePrefillEventHandlersOptions {
  applyRunProgress?: (progress: PrefillProgress) => void;
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  onAuthStateChanged: (state: DaemonAuthState) => void;
  setSession: React.Dispatch<React.SetStateAction<PrefillSessionDto | null>>;
  setTimeRemaining: React.Dispatch<React.SetStateAction<number>>;
  setIsLoggedIn: React.Dispatch<React.SetStateAction<boolean>>;
  /** Drops a session the server has ended and returns the page to its start. */
  endSession: () => void;
  sessionRef: React.RefObject<PrefillSessionDto | null>;
  serviceId: GameServiceId;
  /** Reads the session's run list (GetPrefillRuns) and binds the live bar. */
  rehydratePrefillProgress: (connection: HubConnection, sessionId: string) => Promise<void>;
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
    endSession,
    sessionRef,
    serviceId,
    rehydratePrefillProgress
  } = options;

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

  // Handle auth state changes from backend. Another session's sign-in is not this tab's.
  connection.on(
    getEventName('AuthStateChanged', serviceId),
    ({ sessionId, authState }: { sessionId: string; authState: DaemonAuthState }) => {
      if (sessionId !== sessionRef.current?.id) return;
      onAuthStateChanged(authState);
    }
  );

  // Handle session subscribed confirmation
  connection.on(getEventName('SessionSubscribed', serviceId), (session: PrefillSessionDto) => {
    sessionRef.current = session;
    setSession(session);
    setTimeRemaining(Math.max(0, Math.floor((Date.parse(session.expiresAt) - Date.now()) / 1000)));
    setIsLoggedIn(session.authState === 'Authenticated');
  });

  // Handle session ended (sent to session owner)
  connection.on(
    getEventName('SessionEnded', serviceId),
    ({ reasonKey }: { sessionId: string; reasonKey: string }) => {
      addLog('warning', t('prefill.log.sessionEnded', { reason: t(reasonKey) }));
      endSession();
    }
  );

  // Handle prefill progress updates
  connection.on(
    getEventName('PrefillProgress', serviceId),
    ({
      sessionId,
      progress
    }: {
      sessionId: string;
      progress: PrefillProgress & { totalApps: number };
    }) => {
      if (progress.operationId && progress.daemonInstanceId) {
        if (sessionRef.current?.id === sessionId) options.applyRunProgress?.(progress);
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
      operationId,
      daemonInstanceId,
      run
    }: {
      sessionId: string;
      state: string;
      operationId?: string;
      daemonInstanceId?: string;
      /** The ended run, sent with every per-run event; its reason is what the run card shows. */
      run: PrefillRun;
    }) => {
      if (operationId && daemonInstanceId) {
        if (sessionRef.current?.id !== stateSessionId) return;
        if (state === 'failed') {
          const reasonKey = getPrefillRunReasonKey(run.snapshot.reason, serviceId);
          if (reasonKey) addLog('error', t(reasonKey));
        }
        void rehydratePrefillProgress(connection, stateSessionId);
      }
    }
  );

  // Handle daemon session updates
  connection.on(getEventName('DaemonSessionCreated', serviceId), (session: PrefillSessionDto) => {
    setSession((currentSession) => {
      if (currentSession && session.id === currentSession.id) {
        setTimeRemaining(
          Math.max(0, Math.floor((Date.parse(session.expiresAt) - Date.now()) / 1000))
        );
        return session;
      }
      return currentSession;
    });
  });

  connection.on(getEventName('DaemonSessionUpdated', serviceId), (session: PrefillSessionDto) => {
    setSession((currentSession) => {
      if (currentSession && session.id === currentSession.id) {
        setTimeRemaining(
          Math.max(0, Math.floor((Date.parse(session.expiresAt) - Date.now()) / 1000))
        );
        return session;
      }
      return currentSession;
    });
    // The update's runs only merge into the card, so a run a reset deleted leaves only through the run list.
    // A session whose features are not known yet has no run list to read.
    if (sessionRef.current?.id === session.id && supportsConcurrentPrefill(session))
      void rehydratePrefillProgress(connection, session.id);
  });

  connection.onreconnecting(() => {
    addLog('warning', t('prefill.log.connectionLostReconnecting'));
  });

  connection.onreconnected(async () => {
    addLog('success', t('prefill.log.reconnected'));
    const currentSession = sessionRef.current;
    if (currentSession) {
      try {
        await connection.invoke('SubscribeToSessionAsync', currentSession.id);
      } catch {
        // A failed or slow reply does not end the session; only a server that no longer lists it has ended it.
        // A session it still lists is subscribed once more, because run events go only to subscribers.
        if (connection.state !== 'Connected') return;
        try {
          const listed = await connection.invoke<PrefillSessionDto[]>('GetMySessions');
          if (!listed.some((entry: PrefillSessionDto) => entry.id === currentSession.id)) {
            addLog('warning', t('prefill.log.sessionEndedOnServer'));
            endSession();
            return;
          }
          await connection.invoke('SubscribeToSessionAsync', currentSession.id);
        } catch {
          // The next reconnect or visibility change tries again.
          return;
        }
      }
      try {
        // The run list is read again, because a run can end while the socket is down.
        await rehydratePrefillProgress(connection, currentSession.id);
      } catch {
        // The next live event reads the run list again.
      }
    }
  });
}
