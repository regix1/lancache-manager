import React from 'react';

interface StepDotProps {
  active?: boolean;
  completed?: boolean;
}

/** Step indicator dot shared by the service authentication modals. */
export const StepDot: React.FC<StepDotProps> = ({ active, completed }) => (
  <div
    className={`w-2.5 h-2.5 rounded-full transition duration-200 ${
      active ? 'bg-primary' : completed ? 'bg-success' : 'bg-themed-hover'
    }`}
  />
);
