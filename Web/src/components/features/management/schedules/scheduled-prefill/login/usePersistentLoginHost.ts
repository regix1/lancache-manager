import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { PersistentPrefillServiceId } from '@components/features/prefill/persistentPrefillTypes';
import { useHeldValue } from '@hooks/useHeldValue';
import {
  consumeLoginAttemptNonce,
  ensurePersistentLoginTimeout,
  getPersistentLoginStartPromise,
  getPersistentLoginState,
  isPersistentLoginIntegrationReuse,
  usePersistentLoginRequestNonce
} from '../persistentLoginStore';

export interface PersistentLoginHostProps {
  /** False while the host closes the prompt: the prompt plays its close instead of vanishing. */
  open: boolean;
  isRunning: boolean;
  isAuthenticated: boolean;
  onAuthenticated: () => void;
  autoStart?: boolean;
  onDismiss: () => void;
  /** Starts a new attempt, the way the card's Log in button does. */
  onRetry: () => void;
}

interface PersistentLoginHostState {
  authenticated: boolean;
  dismissed: boolean;
  hasChallenge: boolean;
  loading: boolean;
  error: string | null;
}

interface PersistentLoginHostView {
  opened: boolean;
  /** An attempt ended before the service asked for anything, so the prompt offers a new one. It
   *  lasts through that new attempt until the service asks for something or the sign-in ends. */
  retrying: boolean;
  /** The error to draw: while a retry runs, the one it is retrying, so the prompt keeps its size. */
  error: string | null;
}

interface PersistentLoginHostOptions extends Omit<
  PersistentLoginHostProps,
  'onDismiss' | 'onRetry'
> {
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
  open,
  isRunning,
  isAuthenticated,
  onAuthenticated,
  autoStart = false
}: PersistentLoginHostOptions): PersistentLoginHostView {
  const { t } = useTranslation();
  const loginRequestNonce = usePersistentLoginRequestNonce(service);
  const handledAuthenticatedRef = useRef(false);
  const startInFlightRef = useRef(false);
  // Set when the person asks for a sign-in. A saved-login attempt asks for nothing, so its prompt
  // opens only if the service raises a challenge. A closed host forgets it, so the next attempt's
  // kind decides alone whether the prompt opens.
  const [interactive, setInteractive] = useState(false);
  if (!open && interactive) setInteractive(false);
  const [retrying, setRetrying] = useState(false);
  const heldError = useHeldValue(state.error);

  useEffect(() => {
    if (!state.authenticated) {
      handledAuthenticatedRef.current = false;
      return;
    }

    // A closed host stays mounted only so its prompt can fade out; like the unmounted host it
    // replaces, it reports nothing.
    if (!open || handledAuthenticatedRef.current) {
      return;
    }

    handledAuthenticatedRef.current = true;
    onAuthenticated();
  }, [open, state.authenticated, onAuthenticated]);

  const beginLogin = useCallback(async () => {
    if (startInFlightRef.current) {
      return;
    }

    const current = getPersistentLoginState(service);
    if (current.dismissed) {
      return;
    }
    setInteractive(!isPersistentLoginIntegrationReuse(service));

    if (
      current.pendingChallenge !== null ||
      current.loading ||
      getPersistentLoginStartPromise(service)
    ) {
      // Reveal the admitted challenge, or its admission error and explicit Cancel action.
      // Reopening never starts another attempt or reconstructs its deadline.
      if (current.pendingChallenge !== null) {
        ensurePersistentLoginTimeout(service, {
          noResult: t('prefill.persistent.errors.noResult'),
          timedOut: t('prefill.persistent.loginTimedOut')
        });
      }
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
  }, [resumeModal, service, startLogin, t]);

  // Nonce consumption lives in the store, so remounting cannot restart an attempt already handled.
  useEffect(() => {
    if (!open || !autoStart || !isRunning || isAuthenticated) {
      return;
    }

    if (!consumeLoginAttemptNonce(service, loginRequestNonce)) {
      return;
    }

    void beginLogin();
  }, [open, autoStart, beginLogin, isAuthenticated, isRunning, loginRequestNonce, service]);

  // A sign-in the person asked for keeps its prompt open until they close it or the sign-in
  // succeeds: a failure is shown inside the prompt, so a failed attempt does not open the prompt
  // and then close it again. The loading clause opens it on the render the attempt starts, before
  // `interactive` is set.
  const requested = interactive || (state.loading && !isPersistentLoginIntegrationReuse(service));
  const opened =
    open && !state.dismissed && !state.authenticated && (state.hasChallenge || requested);
  // Retry mode starts when an attempt fails before the service asks for anything. It lasts through
  // the reset and the new attempt a Retry starts, so the prompt keeps its error and its Retry
  // button until the service asks for something, the sign-in ends or the prompt closes.
  const failedBeforeChallenge = !state.loading && !state.hasChallenge && state.error !== null;
  const nextRetrying = opened && !state.hasChallenge && (failedBeforeChallenge || retrying);
  if (nextRetrying !== retrying) setRetrying(nextRetrying);
  return {
    opened,
    retrying: nextRetrying,
    error: state.error ?? (nextRetrying ? heldError : null)
  };
}
