import React, { useCallback, useEffect, useRef } from 'react';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { usePrefillSignalR } from '@components/features/prefill/hooks/usePrefillSignalR';
import { prefillServiceConfig } from '@components/features/prefill/hooks/prefillServiceConfig';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import type { SteamLoginFlowState } from '@hooks/useSteamAuthentication';
import ApiService from '@services/api.service';

interface XboxMappingLoginModalProps {
  /** Close the login flow (unmounts this component, tearing down the daemon hub connection). */
  onClose: () => void;
  /** Called after the catalog has been collected so the parent can reload its status. */
  onAuthenticated?: () => void;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

// usePrefillSignalR is built for the full prefill panel, so it requires activity-log / progress /
// completion callbacks the mapping login does not use. These no-ops neutralise that machinery: no
// log spam, no prefill progress, and `isCompletionDismissed` returns true to suppress any
// "prefill completed while away" banner that an unrelated session might otherwise raise here.
const XBOX_CONFIG = prefillServiceConfig('xbox');
const noop = (): void => {
  /* no-op: the mapping login does not use this prefill-only callback */
};
const suppressCompletionBanner = (): boolean => true;

/**
 * Self-contained Xbox device-code login for the mapping admin page (Management -> Integrations,
 * the Xbox daemon-status card). This mirrors Epic's admin-page login (EpicDaemonStatus +
 * useEpicMappingAuth + EpicAuthModal), but Xbox has no daemon-free auth client - the device-code
 * flow lives in the prefill daemon - so it reuses the existing prefill login stack
 * (usePrefillSignalR + usePrefillSteamAuth + XboxAuthModal) instead of rebuilding it.
 *
 * It establishes an Xbox daemon session, runs the Microsoft device-code login (surfacing the user
 * code + verification URL via XboxAuthModal), and on success collects the shared catalog. No prefill
 * download is ever started: the catalog is gathered from the freshly-authenticated session (the
 * backend also nudges this automatically via OnDaemonAuthenticated), so logs / detection / removal
 * / banners all light up without prefilling.
 *
 * Prefill isolation (this is the important bit):
 *  - The same user has at most one daemon session (maxSessionsPerUser = 1), so this modal can land
 *    on a session the Prefill page already created. We therefore decide ownership from the hook's
 *    own reconnect probe: if the user already had an active session we REUSE it and never tear it
 *    down; only a session this modal created fresh is ended on close.
 *  - If the resolved session is already prefilling we refuse outright - we never start, steal, or
 *    cancel a running prefill's login.
 *  - If it is already authenticated we just collect the catalog (no second login, no teardown).
 *
 * The heavy session/hub machinery only mounts while the modal is open (the parent renders this
 * component conditionally), so the daemon hub is not connected on every Integrations-tab view.
 */
const XboxMappingLoginModal: React.FC<XboxMappingLoginModalProps> = ({
  onClose,
  onAuthenticated,
  onError,
  onSuccess
}) => {
  // Ownership / lifecycle guards.
  const bootstrapRef = useRef(false); // ran the create-or-reuse decision once
  const sessionDecidedRef = useRef(false); // ran the prefilling/authenticated/login decision once
  const succeededRef = useRef(false); // auth (or catalog collection) succeeded - never tear down
  const guardedRef = useRef(false); // refused a prefilling session / handled an already-authed one
  const ownsSessionRef = useRef(false); // THIS modal created a fresh session (safe to end on close)
  const terminalRef = useRef(false); // a terminal error already closed the modal

  // Stable indirection so usePrefillSteamAuth's onError/onTimeout can reach handlers that are
  // defined *after* it (those handlers need authActions, which this hook returns).
  const terminalErrorRef = useRef<(message: string) => void>(noop);
  const deviceTimeoutRef = useRef<() => void>(noop);

  // Collect the shared catalog from the now-authenticated session, then close. RefreshNowAsync is
  // serialized server-side (a gate) with the automatic on-auth nudge, so calling it explicitly here
  // only guarantees the mapping stats update synchronously - it never double-collects.
  const handleAuthenticated = useCallback(async () => {
    try {
      await ApiService.refreshXboxCatalog();
      onSuccess?.('Xbox sign-in successful. Your game library has been updated.');
      onAuthenticated?.();
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to collect Xbox catalog');
    } finally {
      onClose();
    }
  }, [onSuccess, onAuthenticated, onError, onClose]);

  const signalR = usePrefillSignalR({
    onSessionEnd: noop,
    addLog: noop,
    setBackgroundCompletion: noop,
    clearBackgroundCompletion: noop,
    isCompletionDismissed: suppressCompletionBanner,
    onAuthStateChanged: noop,
    clearAllPrefillStorage: noop,
    hubPath: XBOX_CONFIG.hubPath,
    serviceId: 'xbox'
  });

  const { state: authState, actions: authActions } = usePrefillSteamAuth({
    sessionId: signalR.session?.id ?? null,
    hubConnection: signalR.hubConnection.current,
    serviceId: 'xbox',
    onSuccess: () => {
      if (succeededRef.current) return;
      succeededRef.current = true;
      void handleAuthenticated();
    },
    onError: (message: string) => terminalErrorRef.current(message),
    onDeviceConfirmationTimeout: () => deviceTimeoutRef.current()
  });

  // Close + (only when we own a fresh, unauthenticated, non-prefilling session) tear it down so we
  // never leak an idle container, and never disturb a session that belongs to the Prefill page.
  const handleClose = useCallback(() => {
    const connection = signalR.hubConnection.current;
    const activeSessionId = signalR.session?.id;
    if (
      connection &&
      activeSessionId &&
      !succeededRef.current &&
      !guardedRef.current &&
      ownsSessionRef.current
    ) {
      connection.invoke('CancelLoginAsync', activeSessionId).catch(() => {
        /* best-effort: the session also expires on its own timeout */
      });
      connection.invoke('EndSessionAsync', activeSessionId).catch(() => {
        /* best-effort: terminating the container is opportunistic */
      });
    }
    authActions.resetAuthForm();
    onClose();
  }, [signalR.hubConnection, signalR.session, authActions, onClose]);

  // A terminal auth/start failure must not leave the modal stuck in its "connecting" posture
  // (loading is forced while there is no device code yet). Surface it once and close, ending an
  // owned session via handleClose so a failed start never leaks a container.
  const handleTerminalError = useCallback(
    (message: string) => {
      if (terminalRef.current || succeededRef.current) return;
      terminalRef.current = true;
      onError?.(message);
      handleClose();
    },
    [onError, handleClose]
  );

  useEffect(() => {
    terminalErrorRef.current = handleTerminalError;
  }, [handleTerminalError]);

  useEffect(() => {
    deviceTimeoutRef.current = handleClose;
  }, [handleClose]);

  // Surface a failed session creation (connection error) and close.
  useEffect(() => {
    if (signalR.error) {
      handleTerminalError(signalR.error);
    }
  }, [signalR.error, handleTerminalError]);

  // Bootstrap once the hook's reconnect probe has settled (isInitializing flips false only after it
  // has either reconnected to an existing session or found none). If the user already had a session
  // we reuse it (not ours); otherwise we create a fresh one (ours, safe to end on close).
  useEffect(() => {
    if (bootstrapRef.current) return;
    if (signalR.isInitializing) return;
    const connection = signalR.hubConnection.current;
    if (!connection || connection.state !== 'Connected') return;
    bootstrapRef.current = true;

    if (signalR.session) {
      ownsSessionRef.current = false;
    } else {
      ownsSessionRef.current = true;
      void signalR.createSession(noop);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.isInitializing, signalR.session]);

  // Decide what to do with the resolved session exactly once: refuse a prefilling session, collect
  // the catalog for an already-authenticated one, or drive the device-code login otherwise.
  useEffect(() => {
    const session = signalR.session;
    if (!session?.id || !signalR.hubConnection.current) return;
    if (sessionDecidedRef.current) return;
    sessionDecidedRef.current = true;

    if (session.isPrefilling) {
      // Never hijack, start, steal, or cancel a running prefill's login.
      guardedRef.current = true;
      onError?.('An Xbox prefill is in progress. Finish or stop it before mapping.');
      onClose();
      return;
    }

    if (session.authState === 'Authenticated') {
      // Already signed in (likely shared with the Prefill page): just collect the catalog.
      guardedRef.current = true;
      succeededRef.current = true;
      void handleAuthenticated();
      return;
    }

    // Fresh / unauthenticated and idle: surface the Microsoft device code.
    void authActions.handleAuthenticate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signalR.session]);

  // The login auto-starts once the session is ready, so the manual "continue" step is never needed.
  // Keep the modal in its "connecting" posture (action disabled) for the whole pre-device-code phase
  // so the user cannot click an enabled control and double-trigger the login. A terminal error
  // closes the modal (handleTerminalError), so this never hangs indefinitely.
  const modalState: SteamLoginFlowState = {
    ...authState,
    loading: authState.loading || signalR.isCreating || !authState.needsDeviceCode
  };

  return (
    <XboxAuthModal
      opened
      onClose={handleClose}
      state={modalState}
      actions={authActions}
      onCancelLogin={() => authActions.cancelPendingRequest()}
    />
  );
};

export default XboxMappingLoginModal;
