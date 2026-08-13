import { memo } from 'react';
import { useTranslation } from 'react-i18next';
import { useCountdownTimer } from '@hooks/useCountdownTimer';
import { formatTimeRemaining } from '@components/features/prefill/types';

interface PersistentLoginCountdownProps {
  /** Epoch ms the current login attempt expires at (`PersistentLoginStoreState.loginDeadline`).
   *  `null` means no clock is running - including right after a page reload, before a restored
   *  challenge (if any) gets its own timeout armed - so nothing renders rather than a fabricated
   *  value. */
  deadline: number | null;
}

// The last minute of the attempt, where the line switches to the warning color. Matches
// formatTimeRemaining's own switch from "Xm Ys" to a bare "Ys" below a minute, so the color change
// and the format change land together. A flat minute rather than a share of the window, because
// the windows differ: the Xbox device code runs on Microsoft's own expiry, which is longer.
const FINAL_STRETCH_SECONDS = 60;

/**
 * Renders the real remaining time on whichever login attempt this modal is showing: the
 * persistent-container attempt, the in-process login, or the phone-approval wait. Owns its own tick
 * (via useCountdownTimer) inside a memo'd component, copying CountdownDisplay's shape
 * (SchedulesSection.tsx), so the once-a-second re-render stays local instead of re-rendering the
 * auth modal around a 2FA code the user is typing.
 */
export const PersistentLoginCountdown = memo(function PersistentLoginCountdown({
  deadline
}: PersistentLoginCountdownProps) {
  const { t } = useTranslation();
  const nextRunUtc = deadline !== null ? new Date(deadline).toISOString() : null;
  const secondsRemaining = useCountdownTimer(nextRunUtc, false);

  if (deadline === null) {
    return null;
  }

  const isFinalStretch = secondsRemaining <= FINAL_STRETCH_SECONDS;

  return (
    <p
      className={`text-xs ${isFinalStretch ? 'text-warning-text' : 'text-themed-muted'} text-center tabular-nums`}
    >
      {/* Zero gets a finished sentence of its own instead of being poured into "expires in
          {{time}}". formatTimeRemaining answers zero with a word, not a duration, so the two
          together read "expires in Expiring..." in English and stack two expiry clauses in
          Chinese. */}
      {secondsRemaining > 0
        ? t('prefill.persistent.loginTimeRemaining', {
            time: formatTimeRemaining(secondsRemaining)
          })
        : t('prefill.persistent.loginAttemptExpired')}
    </p>
  );
});
