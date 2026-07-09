import { useCallback, useRef } from 'react';
import { usePersistentPrefillAuth } from './usePersistentPrefillAuth';
import { useErrorHandler } from './useErrorHandler';
import { getPersistentLoginSessionId } from '@components/features/management/schedules/scheduled-prefill/persistentLoginStore';
import type { CredentialChallenge } from './usePrefillSteamAuth';
import type { XboxAuthActions, XboxAuthState } from './useXboxMappingAuth';

interface PersistentXboxAuthState extends XboxAuthState {
  error: string | null;
  authenticated: boolean;
  hasChallenge: boolean;
  dismissed: boolean;
}

interface UsePersistentXboxAuthOptions {
  timeoutSeconds?: number;
  onSuccess?: () => void;
  onError?: (message: string) => void;
}

// Floor between successive polls that both observe the SAME still-pending challenge (e.g. the
// device-code the user hasn't confirmed yet). The backend's resume cache (DaemonSession.
// PendingLoginChallenge) can now resolve a 'challenge' poll result INSTANTLY instead of long-polling
// up to timeoutSeconds, so without this floor the loop below would spin hot against the endpoint for
// the entire device-code wait. A genuinely new challenge (or a 'pending' result, which already
// reflects a real backend-side wait) is never delayed.
const SAME_CHALLENGE_POLL_BACKOFF_MS = 2500;

export function usePersistentXboxAuth(options: UsePersistentXboxAuthOptions = {}) {
  const { state: coreState, actions: coreActions } = usePersistentPrefillAuth({
    ...options,
    service: 'Xbox'
  });
  const { notifyError } = useErrorHandler();
  const pollGenerationRef = useRef(0);

  const pollUntilAuthenticated = useCallback(
    async (generation: number): Promise<boolean> => {
      let lastChallengeId: string | null = null;
      // Also stop once nothing is pinned anymore (getPersistentLoginSessionId('Xbox') === null) -
      // this loop has no other way to observe an external reset (a successful auth via SignalR, a
      // container-list retire on a stop, the overall login timeout). Without this check the loop
      // would keep running on its own local generation ref alone and fire one more poll with an
      // empty sessionId, which the backend always rejects with a misleading "sessionId is required"
      // even though the flow already ended cleanly. pollForResult has its own last-resort guard for
      // the same condition (a race can still slip between this check and the call below), but
      // checking here avoids even attempting the doomed request in the common case.
      while (
        pollGenerationRef.current === generation &&
        getPersistentLoginSessionId('Xbox') !== null
      ) {
        const result = await coreActions.poll();
        if (result.status === 'authenticated') {
          return true;
        }

        const challengeId = result.status === 'challenge' ? result.challenge.challengeId : null;
        if (challengeId !== null && challengeId === lastChallengeId) {
          await new Promise((resolve) => setTimeout(resolve, SAME_CHALLENGE_POLL_BACKOFF_MS));
        }
        lastChallengeId = challengeId;
      }

      return false;
    },
    [coreActions]
  );

  const startLogin = useCallback(async (): Promise<CredentialChallenge | null> => {
    pollGenerationRef.current += 1;
    const generation = pollGenerationRef.current;
    coreActions.resetAuthForm();

    const challenge = await coreActions.start();
    if (challenge?.credentialType === 'device-code') {
      void pollUntilAuthenticated(generation).catch((err: unknown) => {
        // A 404 (session gone - diagnostic ADDENDUM) or any other poll failure already ends this
        // loop via the throw from coreActions.poll() and surfaces via state.error or
        // state.sessionUnavailableState; silent here to avoid a duplicate notification.
        notifyError('Xbox persistent login poll failed', err, {
          silent: true,
          logLabel: 'usePersistentXboxAuth pollUntilAuthenticated'
        });
      });
    }

    return challenge;
  }, [coreActions, pollUntilAuthenticated, notifyError]);

  const handleAuthenticate = useCallback(async (): Promise<boolean> => {
    await startLogin();
    return false;
  }, [startLogin]);

  const resetAuthForm = useCallback(() => {
    pollGenerationRef.current += 1;
    coreActions.resetAuthForm();
  }, [coreActions]);

  const cancelLogin = useCallback(async (): Promise<void> => {
    pollGenerationRef.current += 1;
    await coreActions.cancel();
  }, [coreActions]);

  const cancelPendingRequest = useCallback(() => {
    void cancelLogin();
  }, [cancelLogin]);

  const state: PersistentXboxAuthState = {
    loading: coreState.loading,
    needsDeviceCode: coreState.needsDeviceCode,
    deviceUserCode: coreState.deviceUserCode,
    deviceVerificationUri: coreState.deviceVerificationUri,
    error: coreState.error,
    authenticated: coreState.authenticated,
    hasChallenge: coreState.hasChallenge,
    dismissed: coreState.dismissed
  };

  const actions: XboxAuthActions = {
    handleAuthenticate,
    resetAuthForm,
    cancelPendingRequest
  };

  return {
    state,
    actions,
    startLogin,
    cancelLogin,
    dismissModal: coreActions.dismissModal,
    resumeModal: coreActions.resumeModal
  };
}
