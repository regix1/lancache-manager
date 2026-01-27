import React from 'react';

/**
 * Checkbox component with built-in stopPropagation to prevent event bubbling.
 *
 * IMPORTANT - Avoiding Flicker When Filtering Data:
 * When using a checkbox to filter data (e.g., "Show Lifted Bans"), do NOT trigger
 * an API call on every toggle. Instead:
 * 1. Fetch all data once upfront (e.g., getSteamBans(true) to include all items)
 * 2. Use useMemo to filter the data client-side based on checkbox state
 * 3. Render the memoized filtered array
 *
 * This prevents loading state changes and re-renders that cause screen flicker.
 * See PrefillSessionsSection.tsx for an example implementation.
 */

interface CheckboxProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  label?: string;
  variant?: 'default' | 'rounded';
}

export const Checkbox: React.FC<CheckboxProps> = ({
  label,
  variant = 'default',
  className = '',
  onClick,
  ...props
}) => {
  const checkboxClasses = variant === 'rounded' ? 'rounded' : 'themed-checkbox';

  const handleClick = (e: React.MouseEvent<HTMLInputElement>) => {
    e.stopPropagation();
    onClick?.(e);
  };

  if (label) {
    return (
      <label className="flex items-center gap-2 cursor-pointer" onClick={(e) => e.stopPropagation()}>
        <input type="checkbox" className={`${checkboxClasses} ${className}`} onClick={handleClick} {...props} />
        <span className="text-sm text-themed-secondary">{label}</span>
      </label>
    );
  }

  return <input type="checkbox" className={`${checkboxClasses} ${className}`} onClick={handleClick} {...props} />;
};
