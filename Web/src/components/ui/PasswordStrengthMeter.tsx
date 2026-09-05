import { getPasswordStrength } from '@utils/passwordStrength';

interface PasswordStrengthMeterProps {
  password: string;
  weakLabel: string;
  mediumLabel: string;
  strongLabel: string;
}

/**
 * Strength bar + label under a password input. Renders nothing until a password is typed.
 * Scores with the existing `getPasswordStrength`; the two sites this replaces both computed
 * the same score locally and rendered it identically.
 */
export const PasswordStrengthMeter: React.FC<PasswordStrengthMeterProps> = ({
  password,
  weakLabel,
  mediumLabel,
  strongLabel
}) => {
  if (!password) {
    return null;
  }

  const strength = getPasswordStrength(password);
  const label = strength === 'weak' ? weakLabel : strength === 'medium' ? mediumLabel : strongLabel;

  return (
    <div className="flex items-center gap-2 mt-1">
      <div className="flex-1 h-1.5 rounded-full bg-themed-tertiary overflow-hidden">
        <div
          className={`h-full rounded-full transition-[width] duration-150 ${
            strength === 'weak'
              ? 'w-1/3 bg-[var(--theme-error-text)]'
              : strength === 'medium'
                ? 'w-2/3 bg-[var(--theme-warning-text)]'
                : 'w-full bg-[var(--theme-success-text)]'
          }`}
        />
      </div>
      <span
        className={`text-xs ${
          strength === 'weak'
            ? 'text-themed-error'
            : strength === 'medium'
              ? 'text-themed-warning'
              : 'text-themed-success'
        }`}
      >
        {label}
      </span>
    </div>
  );
};
