import ApiService from '@services/api.service';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import type { DaemonSessionUpdatedEvent, EventHandler } from '@contexts/SignalRContext/types';
import {
  getPersistentPrefillAuthStateChangedEvent,
  getPersistentPrefillSessionTerminatedEvent,
  getPersistentPrefillSessionUpdatedEvent,
  isPersistentPrefillAuthenticatedAuthState
} from './persistentPrefillSignalREvents';

export interface PersistentPrefillSignalRFacade {
  on: (eventName: string, handler: EventHandler) => void;
  off: (eventName: string, handler: EventHandler) => void;
}

interface WaitForPersistentContainerAuthOptions {
  signalR: PersistentPrefillSignalRFacade;
  timeoutMs?: number;
  onContainersUpdate?: (containers: PersistentPrefillContainerDto[]) => void;
  signal?: AbortSignal;
}

interface AuthStateChangedPayload {
  sessionId: string;
  authState: string;
}

interface SessionTerminatedPayload {
  sessionId: string;
}

const DEFAULT_TIMEOUT_MS = 60_000;

/**
 * Waits until the persistent container for {@link serviceId} reports authenticated, stops,
 * or the timeout elapses. Uses SignalR auth/session events (no polling); performs an initial
 * and final list fetch so callers receive up-to-date DTOs.
 */
export async function waitForPersistentContainerAuth(
  serviceId: PersistentPrefillServiceId,
  options: WaitForPersistentContainerAuthOptions
): Promise<{
  containers: PersistentPrefillContainerDto[];
  container: PersistentPrefillContainerDto | undefined;
}> {
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const onContainersUpdate = options.onContainersUpdate;

  let containers = await ApiService.getPersistentPrefillContainers();
  onContainersUpdate?.(containers);

  let container = containers.find((item) => item.service === serviceId);
  if (!container?.isRunning || container.isAuthenticated) {
    return { containers, container };
  }

  const sessionId = container.sessionId;
  const authEvent = getPersistentPrefillAuthStateChangedEvent(serviceId);
  const updatedEvent = getPersistentPrefillSessionUpdatedEvent(serviceId);
  const terminatedEvent = getPersistentPrefillSessionTerminatedEvent(serviceId);

  return new Promise((resolve) => {
    let settled = false;

    const finish = async () => {
      if (settled) {
        return;
      }
      settled = true;
      cleanup();

      containers = await ApiService.getPersistentPrefillContainers();
      onContainersUpdate?.(containers);
      container = containers.find((item) => item.service === serviceId);
      resolve({ containers, container });
    };

    const handleAuthStateChanged: EventHandler = (payload) => {
      const event = payload as AuthStateChangedPayload;
      if (event.sessionId !== sessionId) {
        return;
      }
      if (isPersistentPrefillAuthenticatedAuthState(event.authState)) {
        void finish();
      }
    };

    const handleSessionUpdated: EventHandler = (payload) => {
      const event = payload as DaemonSessionUpdatedEvent;
      if (event.id !== sessionId) {
        return;
      }
      if (isPersistentPrefillAuthenticatedAuthState(event.authState)) {
        void finish();
        return;
      }
      if (event.status !== 'Active') {
        void finish();
      }
    };

    const handleSessionTerminated: EventHandler = (payload) => {
      const event = payload as SessionTerminatedPayload;
      if (event.sessionId === sessionId) {
        void finish();
      }
    };

    const handleAbort = () => {
      void finish();
    };

    const cleanup = () => {
      clearTimeout(timeoutId);
      options.signalR.off(authEvent, handleAuthStateChanged);
      options.signalR.off(updatedEvent, handleSessionUpdated);
      options.signalR.off(terminatedEvent, handleSessionTerminated);
      options.signal?.removeEventListener('abort', handleAbort);
    };

    options.signalR.on(authEvent, handleAuthStateChanged);
    options.signalR.on(updatedEvent, handleSessionUpdated);
    options.signalR.on(terminatedEvent, handleSessionTerminated);
    options.signal?.addEventListener('abort', handleAbort);

    const timeoutId = setTimeout(() => {
      void finish();
    }, timeoutMs);
  });
}
