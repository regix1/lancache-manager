import React from 'react';
import { Checkbox } from './Checkbox';

interface SettingRowProps {
  checked: boolean;
  onChange: (checked: boolean) => void;
  label: string;
  description: string;
  disabled?: boolean;
}

/**
 * One settings switch: a checkbox with its title and a line of explanation beside it.
 *
 * The title doubles as the checkbox's accessible name. It sits in its own paragraph rather than a
 * <label>, so without this the input reaches a screen reader unnamed.
 */
export const SettingRow: React.FC<SettingRowProps> = ({
  checked,
  onChange,
  label,
  description,
  disabled = false
}) => (
  <div className="flex items-start gap-3 py-2">
    <div className="pt-0.5">
      <Checkbox
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        disabled={disabled}
        aria-label={label}
      />
    </div>
    <div className="flex-1 min-w-0">
      <p className="text-sm font-medium text-themed-primary">{label}</p>
      <p className="text-xs mt-0.5 text-themed-muted">{description}</p>
    </div>
  </div>
);
