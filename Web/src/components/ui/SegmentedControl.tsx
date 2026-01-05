import React from 'react';

export interface SegmentedControlOption {
  value: string;
  label?: string;
  icon?: React.ReactNode;
  title?: string;
  disabled?: boolean;
}

interface SegmentedControlProps {
  options: SegmentedControlOption[];
  value: string;
  onChange: (value: string) => void;
  size?: 'sm' | 'md';
  showLabels?: boolean | 'responsive'; // true, false, or 'responsive' (hide on mobile)
  fullWidth?: boolean;
  className?: string;
}

export const SegmentedControl: React.FC<SegmentedControlProps> = ({
  options,
  value,
  onChange,
  size = 'md',
  showLabels = false,
  fullWidth = false,
  className = ''
}) => {
  const sizeClasses = {
    sm: {
      container: 'p-0.5',
      button: 'px-2 py-1',
      icon: 14,
      text: 'text-xs'
    },
    md: {
      container: 'p-1',
      button: 'px-3 py-1.5',
      icon: 16,
      text: 'text-xs'
    }
  };

  const sizes = sizeClasses[size];

  return (
    <div
      className={`inline-flex rounded-lg bg-themed-tertiary ${sizes.container} ${fullWidth ? 'w-full' : ''} ${className}`}
    >
      {options.map((option) => {
        const isActive = value === option.value;
        const isDisabled = option.disabled;

        return (
          <button
            key={option.value}
            onClick={() => !isDisabled && onChange(option.value)}
            disabled={isDisabled}
            className={`${sizes.button} rounded-md transition-all flex items-center justify-center gap-1 font-medium whitespace-nowrap ${
              fullWidth ? 'flex-1' : ''
            } ${
              isActive
                ? 'bg-primary shadow-sm'
                : isDisabled
                  ? 'opacity-50 cursor-default'
                  : 'text-themed-secondary hover:text-themed-primary hover:bg-[var(--theme-bg-secondary)]'
            }`}
            style={{
              backgroundColor: isActive ? 'var(--theme-primary)' : 'transparent',
              color: isActive ? 'var(--theme-button-text)' : 'var(--theme-text-primary)',
              cursor: isDisabled ? 'default' : 'pointer'
            }}
            title={option.title || option.label}
          >
            {option.icon && React.isValidElement(option.icon)
              ? React.cloneElement(option.icon as React.ReactElement<{ size?: number }>, {
                  size: sizes.icon
                })
              : option.icon}
            {(showLabels === true || (!option.icon && option.label)) && option.label && (
              <span className={sizes.text}>{option.label}</span>
            )}
            {showLabels === 'responsive' && option.label && (
              <span className={`${sizes.text} hidden lg:inline`}>{option.label}</span>
            )}
          </button>
        );
      })}
    </div>
  );
};

export default SegmentedControl;
