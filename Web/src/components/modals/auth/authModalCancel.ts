interface AuthModalCancelHandlers {
  cancelPendingRequest: () => void;
  resetAuthForm: () => void;
  onCancelLogin?: () => void;
  onClose: () => void;
}

/**
 * Cancels an in-flight login and closes its modal: stops any pending request, clears the typed
 * credentials, tells the caller the login ended, then closes. `handleExplicitCancel` was
 * byte-identical across the Steam, Epic and Xbox auth modals, so it lives here once instead of
 * three times.
 */
export function cancelAuthModalLogin({
  cancelPendingRequest,
  resetAuthForm,
  onCancelLogin,
  onClose
}: AuthModalCancelHandlers): void {
  cancelPendingRequest();
  resetAuthForm();
  onCancelLogin?.();
  onClose();
}
