import React from 'react';
import { StepDot } from './StepDot';

interface LoginStepsProps {
  /** The two step names, already translated, in order. */
  steps: [string, string];
  /** True once the flow has left the first step, whichever step that is per service. */
  pastFirstStep: boolean;
}

/** The two named steps of a service login, with the one it is on marked. */
export const LoginSteps: React.FC<LoginStepsProps> = ({ steps, pastFirstStep }) => (
  <ol className="login-steps">
    <li className="login-steps__item" aria-current={pastFirstStep ? undefined : 'step'}>
      <StepDot number={1} active={!pastFirstStep} completed={pastFirstStep} />
      <span className="login-steps__name">{steps[0]}</span>
    </li>
    <li
      className={`login-steps__connector${pastFirstStep ? ' login-steps__connector--done' : ''}`}
      aria-hidden="true"
    />
    <li className="login-steps__item" aria-current={pastFirstStep ? 'step' : undefined}>
      <StepDot number={2} active={pastFirstStep} />
      <span className="login-steps__name">{steps[1]}</span>
    </li>
  </ol>
);
