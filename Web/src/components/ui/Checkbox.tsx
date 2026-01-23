import React from 'react';

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

export default Checkbox;
