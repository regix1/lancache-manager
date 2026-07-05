import { useEffect } from 'react';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { EventHandler } from '@contexts/SignalRContext/types';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from '@hooks/usePrefillSteamAuth';
import ApiService from '@services/api.service';
import { SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS } from './constants';
import { getPersistentServiceId } from './scheduledPrefillPlatformUi';
import {
  getPersistentPrefillAuthStateChangedEvent,
  getPersistentPrefillCredentialChallengeEvent
} from './persistentPrefillSignalREvents';
import {
  applyPersistentLoginChallenge,
  getPersistentLoginSessionId,
  markPersistentLoginAuthenticated
} from './persistentLoginStore';

const LOGIN_REQUIRED_SERVICE_IDS: readonly PersistentPrefillServiceId[] =
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map(getPersistentServiceId);

// The device-confirmation ack ('confirm') must be sent exactly once per challenge. Tracked at module
// scope so a listener re-registration (containersByService change) cannot re-send it.
const acknowledgedDeviceConfirmationIds = new Set<string>();

interface CredentialChallengePayload {
  sessionId: string;
  challenge: CredentialChallenge;
}

interface AuthStateChangedPayload {
  sessionId: string;
  authState: string;
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
 *
 * RC3 hardening (session 20260703-221336-2070027597): `containersByService` is React state that
 * refreshes on its own cadence, so at the exact moment of a stop-then-start race it can itself
 * still report the just-replaced session for a beat. Once the login store has pinned a sessionId
 * (from this login flow's own login/challenge response - see `applyPersistentLoginChallenge`),
 * that pin is checked too and wins over a stale container lookup: a push for any other session id
 * is dropped even if `containersByService` hasn't caught up yet.
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

    // Shared sessionId fence (RC3): only act on an event whose session matches the currently known
    // container for the service AND the login store's pinned session (when pinned). Drops a stale
    // event or a concurrent guest login for the same platform.
    const sessionMatches = (
      serviceId: PersistentPrefillServiceId,
      eventSessionId: string
    ): boolean => {
      const container = containersByService.get(serviceId);
      if (!container || container.sessionId !== eventSessionId) {
        return false;
      }
      const pinnedSessionId = getPersistentLoginSessionId(serviceId);
      return pinnedSessionId === null || pinnedSessionId === eventSessionId;
    };

    const handlers = LOGIN_REQUIRED_SERVICE_IDS.flatMap((serviceId) => {
      const challengeEvent = getPersistentPrefillCredentialChallengeEvent(serviceId);
      const challengeHandler: EventHandler = (payload) => {
        const event = payload as CredentialChallengePayload;
        if (!sessionMatches(serviceId, event.sessionId)) {
          return;
        }
        applyPersistentLoginChallenge(serviceId, event.challenge, event.sessionId);

        // Auto-send the device-confirmation acknowledgement here, decoupled from the sequential
        // handleAuthenticate chain (which breaks when the manager's WaitForChallenge serves a stale
        // 'password' challenge again after the password was submitted, so it never reaches the
        // device-confirmation step and never sends this ack - the daemon then sits blocked, never
        // polls Steam for the phone approval, and the login times out to NotAuthenticated). The
        // guest/mapping flow has always auto-sent 'confirm' from its own challenge push for exactly
        // this reason. Sent once per challenge id; the daemon no-ops a duplicate.
        if (
          event.challenge.credentialType === 'device-confirmation' &&
          !acknowledgedDeviceConfirmationIds.has(event.challenge.challengeId)
        ) {
          acknowledgedDeviceConfirmationIds.add(event.challenge.challengeId);
          void ApiService.providePersistentCredential(
            serviceId,
            event.challenge,
            'confirm',
            event.sessionId
          ).catch(() => undefined);
        }
      };

      // AuthStateChanged: Authenticated is the reliable, event-driven completion signal (the daemon
      // logged in - e.g. the user just approved a Steam mobile device-confirmation on their phone).
      // Without this listener the persistent flow had NO event path to completion and relied solely
      // on the REST challenge poll, which could miss the transition, so the modal stayed on
      // "Waiting for Confirmation" even though the daemon was already logged in. This gives the
      // persistent flow the same completion path the guest/mapping flow has always had.
      const authEvent = getPersistentPrefillAuthStateChangedEvent(serviceId);
      const authHandler: EventHandler = (payload) => {
        const event = payload as AuthStateChangedPayload;
        if (event.authState !== 'Authenticated' || !sessionMatches(serviceId, event.sessionId)) {
          return;
        }
        markPersistentLoginAuthenticated(serviceId);
      };

      on(challengeEvent, challengeHandler);
      on(authEvent, authHandler);
      return [
        { eventName: challengeEvent, handler: challengeHandler },
        { eventName: authEvent, handler: authHandler }
      ];
    });

    return () => {
      for (const { eventName, handler } of handlers) {
        off(eventName, handler);
      }
    };
  }, [enabled, on, off, containersByService]);
}
