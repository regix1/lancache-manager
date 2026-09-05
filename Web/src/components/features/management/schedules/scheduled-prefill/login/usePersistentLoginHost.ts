import { useCallback, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import {
  consumeLoginAttemptNonce,
  ensurePersistentLoginTimeout,
  isPersistentLoginIntegrationReuse,
  usePersistentLoginRequestNonce
} from '../persistentLoginStore';

export interface PersistentLoginHostProps {
  isRunning: boolean;
  isAuthenticated: boolean;
  onAuthenticated: () => void;
  autoStart?: boolean;
  onDismiss?: () => void;
}

interface PersistentLoginHostState {
  authenticated: boolean;
  dismissed: boolean;
  hasChallenge: boolean;
  loading: boolean;
}

interface PersistentLoginHostOptions extends PersistentLoginHostProps {
  service: PersistentPrefillServiceId;
  state: PersistentLoginHostState;
  startLogin: () => void | Promise<unknown>;
  resumeModal: () => void;
}

/**
 * Shared lifecycle for the Steam, Epic, and Xbox persistent-login modal hosts. The auth hook and
 * modal differ by platform; nonce consumption, single-flight start, resume, and authenticated
 * notification semantics do not.
 */
export function usePersistentLoginHost({
  service,
  state,
  startLogin,
  resumeModal,
  isRunning,
  isAuthenticated,
  onAuthenticated,
  autoStart = false
}: PersistentLoginHostOptions): boolean {
  const { t } = useTranslation();
  const loginRequestNonce = usePersistentLoginRequestNonce(service);
  const handledAuthenticatedRef = useRef(false);
  const startInFlightRef = useRef(false);

  useEffect(() => {
    if (!state.authenticated) {
      handledAuthenticatedRef.current = false;
      return;
    }

    if (handledAuthenticatedRef.current) {
      return;
    }

    handledAuthenticatedRef.current = true;
    onAuthenticated();
  }, [state.authenticated, onAuthenticated]);

  const beginLogin = useCallback(async () => {
    if (startInFlightRef.current) {
      return;
    }

    if (state.hasChallenge) {
      // A challenge is already pending for this service: reveal it instead of starting over.
      // Only start() arms the overall ceiling, so a challenge that reached the store any other way
      // has none - most importantly one restored from the backend's pending-challenge cache after
      // a page reload, which PersistentLoginHost auto-resumes through here. Arming it on the
      // resume keeps that attempt bounded; a challenge whose clock is already running is left on
      // its original deadline, so re-showing the modal cannot extend it.
      ensurePersistentLoginTimeout(service, t('prefill.persistent.loginTimedOut'));
      resumeModal();
      return;
    }

    startInFlightRef.current = true;
    resumeModal();
    try {
      await startLogin();
    } finally {
      startInFlightRef.current = false;
    }
  }, [resumeModal, service, startLogin, state.hasChallenge, t]);

  // Nonce consumption lives in the store, so remounting cannot restart an attempt already handled.
  useEffect(() => {
    if (!autoStart || !isRunning || isAuthenticated) {
      return;
    }

    if (!consumeLoginAttemptNonce(service, loginRequestNonce)) {
      return;
    }

    void beginLogin();
  }, [autoStart, beginLogin, isAuthenticated, isRunning, loginRequestNonce, service]);

  return (
    !state.dismissed &&
    !state.authenticated &&
    (state.hasChallenge || (state.loading && !isPersistentLoginIntegrationReuse(service)))
  );
}
