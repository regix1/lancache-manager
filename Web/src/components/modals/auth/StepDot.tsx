import React from 'react';

interface StepDotProps {
  number: number;
  active?: boolean;
  completed?: boolean;
}

/** Numbered step circle shared by the service authentication modals: blue for the current step,
 *  green for a finished one, muted for the one still ahead. */
export const StepDot: React.FC<StepDotProps> = ({ number, active, completed }) => (
  <span
    className={`inline-flex items-center justify-center flex-shrink-0 w-6 h-6 rounded-full border text-xs font-semibold tabular-nums ${
      active
        ? 'bg-primary border-transparent text-themed-button'
        : completed
          ? 'bg-[var(--theme-success-muted)] border-transparent text-success-text'
          : 'bg-themed-tertiary border-themed-secondary text-themed-muted'
    }`}
  >
    {number}
  </span>
);
