import { useEffect } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { EventHandler } from '@contexts/SignalRContext/types';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from '@hooks/usePrefillSteamAuth';
import { SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS } from './constants';
import { getPersistentServiceId } from './scheduledPrefillPlatformUi';
import { getPersistentPrefillCredentialChallengeEvent } from './persistentPrefillSignalREvents';
import { applyPersistentLoginChallenge } from './persistentLoginStore';

const LOGIN_REQUIRED_SERVICE_IDS: readonly PersistentPrefillServiceId[] =
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map(getPersistentServiceId);

interface CredentialChallengePayload {
  sessionId: string;
  challenge: CredentialChallenge;
}

interface UsePersistentLoginChallengeSignalROptions {
  /** When false, listeners are not registered. */
  enabled: boolean;
  /** Current persistent container per service, used to filter events by sessionId. */
  containersByService: Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>;
}

/**
 * Delivers daemon credential challenges (username/2FA/device-code/etc.) into the persistent login
 * store the instant the daemon emits them, via the same CredentialChallenge event family the
 * mapping-flow live login already listens for - PrefillDaemonServiceBase.Notifications.cs now
 * mirrors it to this hub too, alongside AuthStateChanged/SessionUpdated. The REST challenge poll
 * (usePersistentPrefillAuth's getPersistentChallenge) stays wired as the fallback for when SignalR
 * is disconnected; this hook only ever writes into the store the same way that poll already does.
 *
 * Filters by sessionId against the currently known persistent container for the service, so a
 * concurrent guest login for the same platform (a different session, same event family) can never
 * leak its challenge into the admin's persistent login flow, and a stale event for a service with
 * no login pending is silently ignored.
 */
export function usePersistentLoginChallengeSignalR({
  enabled,
  containersByService
}: UsePersistentLoginChallengeSignalROptions): void {
  const { on, off } = useSignalR();

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const handlers = LOGIN_REQUIRED_SERVICE_IDS.map((serviceId) => {
      const eventName = getPersistentPrefillCredentialChallengeEvent(serviceId);
      const handler: EventHandler = (payload) => {
        const event = payload as CredentialChallengePayload;
        const container = containersByService.get(serviceId);
        if (!container || container.sessionId !== event.sessionId) {
          return;
        }
        applyPersistentLoginChallenge(serviceId, event.challenge);
      };
      on(eventName, handler);
      return { eventName, handler };
    });

    return () => {
      for (const { eventName, handler } of handlers) {
        off(eventName, handler);
      }
    };
  }, [enabled, on, off, containersByService]);
}
