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
      container: 'p-[3px]',
      button: 'px-2 py-1 rounded-md',
      icon: 14,
      text: 'text-xs'
    },
    md: {
      container: 'p-[3px]',
      button: 'px-3 py-[0.4rem] rounded-[7px]',
      icon: 14,
      text: 'text-xs'
    }
  };

  const sizes = sizeClasses[size];

  return (
    <div
      className={`inline-flex rounded-[10px] ${sizes.container} ${fullWidth ? 'w-full' : ''} ${className}`}
      style={{
        backgroundColor: 'var(--theme-bg-tertiary)',
        border: '1px solid var(--theme-border-secondary)'
      }}
    >
      {options.map((option) => {
        const isActive = value === option.value;
        const isDisabled = option.disabled;

        return (
          <button
            key={option.value}
            onClick={() => !isDisabled && onChange(option.value)}
            disabled={isDisabled}
            className={`${sizes.button} transition-all flex items-center justify-center gap-[0.4rem] font-semibold whitespace-nowrap ${
              fullWidth ? 'flex-1' : ''
            } ${
              isDisabled && !isActive ? 'opacity-50 cursor-default' : ''
            }`}
            style={{
              backgroundColor: isActive ? 'var(--theme-primary)' : 'transparent',
              color: isActive ? 'var(--theme-button-text)' : 'var(--theme-text-muted)',
              cursor: isDisabled ? 'default' : 'pointer',
              boxShadow: isActive ? '0 2px 4px color-mix(in srgb, var(--theme-primary) 25%, transparent)' : 'none',
              fontSize: '0.75rem'
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
