import React from 'react';
import { Alert } from '@components/ui/Alert';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { PersistentLoginCountdown } from './PersistentLoginCountdown';

interface LoginAttemptStatusProps {
  /** What the login attempt is doing or waiting on right now, already translated. An empty string
   *  in the states with nothing to report leaves the strip holding only the time left. */
  label: string;
  /** True while a request is in flight. Draws the dialog's only spinner beside the label. */
  busy: boolean;
  /** Epoch ms this login attempt expires at, drawn as the time left on the right of the strip.
   *  `null` draws nothing there. */
  deadline: number | null;
  /** Why the last attempt did not work, already translated, or null while it is going fine. Drawn
   *  under the strip; a wrong password or code keeps the prompt open on the same step. */
  error?: string | null;
  /** The failed attempt's first line, already translated, for example 'Failed to sign in to Steam'.
   *  Drawn as the red Alert's title above `error`. */
  errorTitle: string;
}

/** The status strip of a login attempt: what it is doing on the left, the time left on the right.
 *  Every caller renders this in every state of its login, because the left side is the live region
 *  and a region a screen reader has not seen before cannot announce the text it was created holding.
 *  The countdown sits outside that region so its tick is not announced. The strip keeps one height
 *  in every state, so going from idle to working moves the footer no pixels at all. */
export const LoginAttemptStatus: React.FC<LoginAttemptStatusProps> = ({
  label,
  busy,
  deadline,
  error,
  errorTitle
}) => (
  <div className="login-attempt-slot">
    <div className="well-surface login-attempt-status text-themed-muted">
      <span className="flex items-center gap-2 min-w-0" role="status" aria-live="polite">
        {busy ? <LoadingSpinner inline size="sm" /> : null}
        {label}
      </span>
      <PersistentLoginCountdown deadline={deadline} />
    </div>
    {error ? (
      <Alert color="red" title={errorTitle}>
        {error}
      </Alert>
    ) : null}
  </div>
);
