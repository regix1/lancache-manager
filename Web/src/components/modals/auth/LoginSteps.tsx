import React from 'react';
import { StepDot } from './StepDot';
import { PersistentLoginCountdown } from './PersistentLoginCountdown';

interface LoginStepsProps {
  /** Which container account this login signs in to. `null` outside the persistent-container flow,
   *  where the login is not scoped to a container and needs no line. */
  notice: string | null;
  /** Epoch ms this login attempt expires at (`PersistentLoginStoreState.loginDeadline`). */
  deadline: number | null;
  /** True once the flow has left the first step, whichever step that is per service. */
  pastFirstStep: boolean;
}

/** The head of every service auth modal body: whose login this is, how long is left on it, and
 *  which of the two steps it is on. */
export const LoginSteps: React.FC<LoginStepsProps> = ({ notice, deadline, pastFirstStep }) => (
  <div className="space-y-4">
    {notice && <p className="text-xs text-themed-muted text-center">{notice}</p>}
    <PersistentLoginCountdown deadline={deadline} />
    <div className="flex items-center justify-center gap-2">
      <StepDot active={!pastFirstStep} completed={pastFirstStep} />
      <div className="w-8 h-px bg-themed-tertiary" />
      <StepDot active={pastFirstStep} />
    </div>
  </div>
);
