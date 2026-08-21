import type { ReactNode } from 'react';

interface StepHeaderProps {
  /** Icon element, already sized and coloured by the caller, e.g. `<CheckCircle className="w-7 h-7 icon-success" />`. */
  icon: ReactNode;
  /** Circle fill class, e.g. `bg-themed-info`. Six real values across the setup steps. */
  iconBackground: string;
  title: string;
  description: string;
}

/**
 * Centred icon-circle + heading + description block that opens every setup wizard step.
 */
export const StepHeader: React.FC<StepHeaderProps> = ({
  icon,
  iconBackground,
  title,
  description
}) => (
  <div className="flex flex-col items-center text-center">
    <div
      className={`w-14 h-14 rounded-full flex items-center justify-center mb-3 ${iconBackground}`}
    >
      {icon}
    </div>
    <h3 className="text-lg font-semibold text-themed-primary mb-1">{title}</h3>
    <p className="text-sm text-themed-secondary max-w-md">{description}</p>
  </div>
);
