import React from 'react';
import { Alert } from '@components/ui/Alert';
import LoadingSpinner from '@components/common/LoadingSpinner';

interface LoginAttemptStatusProps {
  /** What the login attempt is waiting on right now, already translated. An empty string in the
   *  states that wait on nothing, which collapses the row away. */
  label: string;
  /** The step's one line, already translated. Drawn in the same slot while nothing is in flight,
   *  and never inside the live region, so it is not announced when a failed submit clears the
   *  label back to it. */
  note?: string;
  /** Why the last attempt did not work, already translated, or null while it is going fine. Takes
   *  the slot in place of the note: a login is either working on something or explaining why it
   *  stopped, never both, because every submit clears the previous answer before it starts. */
  error?: string | null;
}

/** The live status of a login attempt, on its own line. Every caller renders this in every state
 *  of its login, because the row is the live region and a region a screen reader has not seen
 *  before cannot announce the text it was created holding. The slot around it keeps that line the
 *  same height in every state, so going from idle to working moves the panel no pixels at all. */
export const LoginAttemptStatus: React.FC<LoginAttemptStatusProps> = ({ label, note, error }) => (
  <div className="login-attempt-slot">
    <div
      className="well-surface login-attempt-status text-themed-muted"
      role="status"
      aria-live="polite"
    >
      {label ? (
        <>
          <LoadingSpinner inline size="sm" />
          <span className="text-sm">{label}</span>
        </>
      ) : null}
    </div>
    {error ? <Alert color="red">{error}</Alert> : null}
    {!error && !label && note ? (
      <p className="text-sm text-themed-muted text-center">{note}</p>
    ) : null}
  </div>
);
