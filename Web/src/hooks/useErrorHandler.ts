import { useCallback } from 'react';
import { useNotifications } from '@contexts/notifications';
import { getErrorMessage, isAbortError } from '@utils/error';

/** Options for {@link ErrorHandler.notifyError}. */
interface NotifyErrorOptions {
  /**
   * When true, log the raw error for debugging but do NOT surface a notification. Use only for
   * genuine background noise (e.g. poll retries) - silence is then a reviewable decision, not an
   * accidental `console.error`.
   */
  silent?: boolean;
  /** Console label for the raw error; defaults to the user-facing message. */
  logLabel?: string;
}

/** Strongly-typed error sink returned by {@link useErrorHandler}. */
interface ErrorHandler {
  /**
   * Surface a failure to the user.
   * @param userMessage Already-translated, friendly message that is what actually renders.
   * @param error       The raw caught value; its technical detail goes to the console only.
   * @param opts        Optional silencing / logging controls.
   */
  notifyError: (userMessage: string, error?: unknown, opts?: NotifyErrorOptions) => void;
}

/**
 * The one shared error-surfacing hook. It COMPOSES the existing pieces - `getErrorMessage` +
 * `useNotifications().addNotification` - and adds NO new channel; every failure it surfaces is the
 * canonical generic/failed notification (`{ type:'generic', status:'failed', ... }`) that the
 * unified registry already renders.
 *
 * Routing (see the error-handling standard §4.2): transient / one-shot action failures (button
 * clicks: auth, save, revoke, import) go here. Cancellation (`AbortError`) is swallowed - it is a
 * distinct terminal outcome, not a failure. The raw technical detail is logged to the console for
 * debugging; the translated `userMessage` is what the user sees (never a raw `err.message`).
 */
export function useErrorHandler(): ErrorHandler {
  const { addNotification } = useNotifications();

  const notifyError = useCallback(
    (userMessage: string, error?: unknown, opts: NotifyErrorOptions = {}): void => {
      // Cancellation is not an error - mirror the API layer's isAbortError guard.
      if (error !== undefined && isAbortError(error)) {
        return;
      }

      // Raw technical detail goes to the console only (never rendered); the translated userMessage
      // is what surfaces. Logged even when silent so nothing is truly swallowed.
      const detail = error !== undefined ? getErrorMessage(error) : undefined;
      console.error(opts.logLabel ?? userMessage, detail ?? error);

      if (opts.silent) {
        return;
      }

      addNotification({
        type: 'generic',
        status: 'failed',
        message: userMessage,
        details: { notificationType: 'error' }
      });
    },
    [addNotification]
  );

  return { notifyError };
}
