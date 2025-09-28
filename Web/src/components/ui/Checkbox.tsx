import React from 'react';

interface CheckboxProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  label?: string;
  variant?: 'default' | 'rounded';
}

export const Checkbox: React.FC<CheckboxProps> = ({
  label,
  variant = 'default',
  className = '',
  ...props
}) => {
  const checkboxClasses = variant === 'rounded' ? 'rounded' : 'themed-checkbox';

  if (label) {
    return (
      <label className="flex items-center gap-2 cursor-pointer">
        <input
          type="checkbox"
          className={`${checkboxClasses} ${className}`}
          {...props}
        />
        <span className="text-sm text-themed-secondary">{label}</span>
      </label>
    );
  }

  return (
    <input
      type="checkbox"
      className={`${checkboxClasses} ${className}`}
      {...props}
    />
  );
};

export default Checkbox;