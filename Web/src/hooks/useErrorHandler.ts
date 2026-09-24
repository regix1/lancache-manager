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
   * @param userMessage Already-translated, friendly message: the popup's first line.
   * @param error       The raw caught value; `getErrorMessage` of it is the popup's second line
   *                    and is logged to the console.
   * @param opts        Optional silencing / logging controls.
   */
  notifyError: (userMessage: string, error?: unknown, opts?: NotifyErrorOptions) => void;
}

/** Strongly-typed success sink returned by {@link useNotifySuccess}. */
interface SuccessNotifier {
  /**
   * Surface a completed action to the user.
   * @param message Already-translated confirmation text that is what actually renders.
   */
  notifySuccess: (message: string) => void;
}

/**
 * The one shared error-surfacing hook. It COMPOSES the existing pieces - `getErrorMessage` +
 * `useNotifications().addNotification` - and adds NO new channel; every failure it surfaces is the
 * canonical generic/failed notification (`{ type:'generic', status:'failed', ... }`) that the
 * unified registry already renders.
 *
 * Routing (see the error-handling standard §4.2): transient / one-shot action failures (button
 * clicks: auth, save, revoke, import) go here. Cancellation (`AbortError`) is swallowed - it is a
 * distinct terminal outcome, not a failure. The translated `userMessage` is the first line the user
 * sees and `getErrorMessage(error)` is the reason on the second line (never a raw `err.message`);
 * with no error the popup has one line. The reason is also logged to the console for debugging.
 */
export function useErrorHandler(): ErrorHandler {
  const { addNotification } = useNotifications();

  const notifyError = useCallback(
    (userMessage: string, error?: unknown, opts: NotifyErrorOptions = {}): void => {
      // Cancellation is not an error - mirror the API layer's isAbortError guard.
      if (error !== undefined && isAbortError(error)) {
        return;
      }

      // The reason renders as the popup's second line; the console gets it too, even when silent,
      // so nothing is truly swallowed.
      const detail = error !== undefined ? getErrorMessage(error) : undefined;
      console.error(opts.logLabel ?? userMessage, detail ?? error);

      if (opts.silent) {
        return;
      }

      addNotification({
        type: 'generic',
        status: 'failed',
        message: userMessage,
        error: detail,
        details: { notificationType: 'error' }
      });
    },
    [addNotification]
  );

  return { notifyError };
}

/**
 * Sibling to {@link useErrorHandler} for the success half of the same notification family.
 * COMPOSES the existing `addNotification` channel and adds no new one - every success it
 * surfaces is the same generic/completed notification the unified registry already renders.
 */
export function useNotifySuccess(): SuccessNotifier {
  const { addNotification } = useNotifications();

  const notifySuccess = useCallback(
    (message: string): void => {
      addNotification({
        type: 'generic',
        status: 'completed',
        message,
        details: { notificationType: 'success' }
      });
    },
    [addNotification]
  );

  return { notifySuccess };
}
