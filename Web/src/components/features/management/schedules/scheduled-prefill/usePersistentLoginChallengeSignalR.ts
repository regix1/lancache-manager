import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import type { EventHandler } from '@contexts/SignalRContext/types';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import type { CredentialChallenge } from '@hooks/usePrefillSteamAuth';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import ApiService from '@services/api.service';
import type { PrefillLoginEnding } from '@/types';
import { getErrorMessage } from '@utils/error';
import { SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS } from './constants';
import { getPersistentServiceId } from './scheduledPrefillPlatformUi';
import {
  getPersistentPrefillAuthStateChangedEvent,
  getPersistentPrefillCredentialChallengeEvent,
  getPersistentPrefillSessionUpdatedEvent
} from './persistentPrefillSignalREvents';
import {
  applyPersistentLoginChallenge,
  getPersistentLoginSessionId,
  getPersistentLoginStartRequest,
  getPersistentLoginState,
  hasPersistentLoginIntent,
  isPersistentLoginAuthenticatedResponse,
  isPersistentLoginCredentialChallenge,
  markPersistentLoginAuthenticated,
  resetPersistentLoginSessionReplaced,
  updatePersistentLoginState,
  type PersistentLoginWaitKey
} from './persistentLoginStore';

const LOGIN_REQUIRED_SERVICE_IDS: readonly PersistentPrefillServiceId[] =
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map(getPersistentServiceId);

// The device-confirmation ack ('confirm') must be sent exactly once per challenge. Tracked at module
// scope so a listener re-registration (containersByService change) cannot re-send it.
const acknowledgedDeviceConfirmationIds = new Set<string>();

// Session pushes seen per service. A reconnect read that was sent before a later push is older than
// that push, so its answer must not overwrite what the push wrote.
const sessionPushCounts = new Map<PersistentPrefillServiceId, number>();

// Shared sessionId fence: only act on an event whose session matches the currently known
// container for the service AND the login store's pinned session (when pinned). Drops a stale
// event or a concurrent guest login for the same platform.
function sessionMatches(
  containersByService: Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>,
  serviceId: PersistentPrefillServiceId,
  eventSessionId: string
): boolean {
  const container = containersByService.get(serviceId);
  if (!container || container.sessionId !== eventSessionId) {
    return false;
  }
  if (!hasPersistentLoginIntent(serviceId)) {
    return false;
  }
  const pinnedSessionId = getPersistentLoginSessionId(serviceId);
  const requestedSessionId = getPersistentLoginStartRequest(serviceId)?.sessionId;
  return (pinnedSessionId ?? requestedSessionId) === eventSessionId;
}

// Sends the device-confirmation ack ('confirm') once per challenge id. A failed send frees the id, so
// the next push or reconnect read of the same challenge sends it again, and shows the error in the
// sign-in while that challenge still waits. A send that succeeds while that challenge still waits
// clears the error a failed send of it wrote, since the approval wait goes on; any other failure
// written since stays.
function acknowledgeDeviceConfirmation(
  serviceId: PersistentPrefillServiceId,
  challenge: CredentialChallenge,
  sessionId: string
): void {
  if (
    challenge.credentialType !== 'device-confirmation' ||
    acknowledgedDeviceConfirmationIds.has(challenge.challengeId)
  )
    return;
  acknowledgedDeviceConfirmationIds.add(challenge.challengeId);
  void ApiService.providePersistentCredential(serviceId, challenge, 'confirm', sessionId).then(
    () => {
      const state = getPersistentLoginState(serviceId);
      // A sign-in whose clock is gone keeps its error: it no longer waits on this approval.
      if (
        state.pendingChallenge?.challengeId !== challenge.challengeId ||
        state.loginDeadline === null ||
        state.confirmationErrorChallengeId !== challenge.challengeId
      )
        return;
      updatePersistentLoginState(serviceId, (current) => ({
        ...current,
        error: null,
        confirmationErrorChallengeId: null
      }));
    },
    (error: unknown) => {
      acknowledgedDeviceConfirmationIds.delete(challenge.challengeId);
      if (
        getPersistentLoginState(serviceId).pendingChallenge?.challengeId !== challenge.challengeId
      )
        return;
      updatePersistentLoginState(serviceId, (current) => ({
        ...current,
        error: getErrorMessage(error),
        confirmationErrorChallengeId: challenge.challengeId
      }));
    }
  );
}

function writeLoginWaitKey(
  serviceId: PersistentPrefillServiceId,
  loginWaitKey: PersistentLoginWaitKey | null
): void {
  if (getPersistentLoginState(serviceId).loginWaitKey !== loginWaitKey)
    updatePersistentLoginState(serviceId, (current) => ({ ...current, loginWaitKey }));
}

// A sign-in ending, pushed with the session or read after a reconnect, ends the dialog only when it belongs to the attempt
// the dialog waits on: success closes it signed in, any other ending clears the prompt and shows its reason.
function endPersistentLoginOnEnding(
  serviceId: PersistentPrefillServiceId,
  ending: PrefillLoginEnding,
  reason: string
): void {
  if (getPersistentLoginState(serviceId).pendingChallenge?.loginAttempt !== ending.loginAttempt)
    return;
  if (ending.status === 'completed') markPersistentLoginAuthenticated(serviceId);
  else resetPersistentLoginSessionReplaced(serviceId, reason);
}

interface CredentialChallengePayload {
  sessionId: string;
  challenge: CredentialChallenge;
}

interface AuthStateChangedPayload {
  sessionId: string;
  authState: string;
}

interface UpdatedSession {
  id: string;
  loginEnding?: PrefillLoginEnding;
  loginWaitKey: PersistentLoginWaitKey | null;
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
 * mirrors it to this hub too, alongside AuthStateChanged/SessionUpdated. The REST challenge read
 * (getPersistentChallenge) is no longer a poll: it runs once when the connection comes back, to pick up
 * whatever was pushed while the socket was down.
 *
 * Filters by sessionId against the currently known persistent container for the service, so a
 * concurrent guest login for the same platform (a different session, same event family) can never
 * leak its challenge into the admin's persistent login flow, and a stale event for a service with
 * no login pending is silently ignored.
 *
 * `containersByService` is React state that
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
  const { on, off, isConnected } = useSignalR();
  const { t } = useTranslation();

  useEffect(() => {
    if (!enabled) {
      return;
    }

    const handlers = LOGIN_REQUIRED_SERVICE_IDS.flatMap((serviceId) => {
      const challengeEvent = getPersistentPrefillCredentialChallengeEvent(serviceId);
      const challengeHandler: EventHandler = (payload) => {
        const event = payload as CredentialChallengePayload;
        if (!sessionMatches(containersByService, serviceId, event.sessionId)) {
          return;
        }
        if (
          !applyPersistentLoginChallenge(
            serviceId,
            event.challenge,
            {
              noResult: t('prefill.persistent.errors.noResult'),
              timedOut: t('prefill.persistent.loginTimedOut')
            },
            event.sessionId
          )
        )
          return;

        // Auto-send the device-confirmation acknowledgment here, decoupled from the sequential
        // handleAuthenticate chain (which breaks when the manager's WaitForChallenge serves a stale
        // 'password' challenge again after the password was submitted, so it never reaches the
        // device-confirmation step and never sends this ack - the daemon then sits blocked, never
        // polls Steam for the phone approval, and the login times out to NotAuthenticated). The
        // guest/mapping flow has always auto-sent 'confirm' from its own challenge push for exactly
        // this reason. Sent once per challenge id; the daemon no-ops a duplicate.
        acknowledgeDeviceConfirmation(serviceId, event.challenge, event.sessionId);
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
        if (
          event.authState !== 'Authenticated' ||
          !sessionMatches(containersByService, serviceId, event.sessionId)
        ) {
          return;
        }
        markPersistentLoginAuthenticated(serviceId);
      };

      const sessionUpdatedEvent = getPersistentPrefillSessionUpdatedEvent(serviceId);
      const sessionUpdatedHandler: EventHandler = (message) => {
        const session = message as UpdatedSession;
        if (!sessionMatches(containersByService, serviceId, session.id)) return;
        sessionPushCounts.set(serviceId, (sessionPushCounts.get(serviceId) ?? 0) + 1);
        writeLoginWaitKey(serviceId, session.loginWaitKey);
        if (!session.loginEnding) return;
        endPersistentLoginOnEnding(serviceId, session.loginEnding, t(session.loginEnding.stageKey));
      };

      on(challengeEvent, challengeHandler);
      on(authEvent, authHandler);
      on(sessionUpdatedEvent, sessionUpdatedHandler);
      return [
        { eventName: challengeEvent, handler: challengeHandler },
        { eventName: authEvent, handler: authHandler },
        { eventName: sessionUpdatedEvent, handler: sessionUpdatedHandler }
      ];
    });

    return () => {
      for (const { eventName, handler } of handlers) {
        off(eventName, handler);
      }
    };
  }, [enabled, on, off, containersByService, t]);

  // The pushes above are the only way a waiting sign-in moves; after a reconnect, whatever was pushed while the socket was
  // down is read once. A failed read leaves the dialog to the next push or its own deadline.
  useReconnectRefetch(isConnected, () => {
    if (!enabled) return;
    const messages = {
      noResult: t('prefill.persistent.errors.noResult'),
      timedOut: t('prefill.persistent.loginTimedOut')
    };
    for (const serviceId of LOGIN_REQUIRED_SERVICE_IDS) {
      const sessionId = getPersistentLoginSessionId(serviceId);
      const challenge = getPersistentLoginState(serviceId).pendingChallenge;
      if (!sessionId || !challenge) continue;
      void ApiService.getPersistentChallenge(serviceId, sessionId, challenge.loginAttempt).then(
        (response) => {
          if (getPersistentLoginSessionId(serviceId) !== sessionId) return;
          if (isPersistentLoginAuthenticatedResponse(response))
            markPersistentLoginAuthenticated(serviceId);
          else if (isPersistentLoginCredentialChallenge(response)) {
            if (applyPersistentLoginChallenge(serviceId, response, messages, sessionId))
              acknowledgeDeviceConfirmation(serviceId, response, sessionId);
          } else if (typeof response === 'object' && 'loginEnding' in response)
            endPersistentLoginOnEnding(
              serviceId,
              response.loginEnding,
              t(response.loginEnding.stageKey)
            );
        },
        () => undefined
      );
    }

    // What a waiting Log in waits for arrives only with a session push, and no challenge exists yet
    // to read it from, so the session itself is read.
    const waiting = LOGIN_REQUIRED_SERVICE_IDS.flatMap((serviceId) => {
      const sessionId = containersByService.get(serviceId)?.sessionId;
      return sessionId && sessionMatches(containersByService, serviceId, sessionId)
        ? [{ serviceId, sessionId, pushes: sessionPushCounts.get(serviceId) ?? 0 }]
        : [];
    });
    if (waiting.length === 0) return;
    void ApiService.getActivePrefillSessions().then(
      (sessions) => {
        for (const { serviceId, sessionId, pushes } of waiting) {
          // A session the caller may not see is left out of the answer, which says nothing about its key.
          const session = sessions.find((candidate) => candidate.id === sessionId);
          if (
            !session ||
            (sessionPushCounts.get(serviceId) ?? 0) !== pushes ||
            !sessionMatches(containersByService, serviceId, sessionId)
          )
            continue;
          writeLoginWaitKey(serviceId, session.loginWaitKey ?? null);
        }
      },
      () => undefined
    );
  });
}
