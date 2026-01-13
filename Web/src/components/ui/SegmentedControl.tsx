import React from 'react';
import { Tooltip } from './Tooltip';

export interface SegmentedControlOption {
  value: string;
  label?: string;
  icon?: React.ReactNode;
  tooltip?: string;
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
      className={`inline-flex rounded-[10px] ${sizes.container} ${fullWidth ? 'w-full' : ''} ${className} bg-themed-tertiary border border-themed-secondary`}
    >
      {options.map((option) => {
        const isActive = value === option.value;
        const isDisabled = option.disabled;

        const buttonElement = (
          <button
            key={option.value}
            onClick={() => !isDisabled && onChange(option.value)}
            disabled={isDisabled}
            className={`${sizes.button} transition-all flex items-center justify-center gap-[0.4rem] font-semibold whitespace-nowrap text-xs ${
              fullWidth ? 'flex-1' : ''
            } ${
              isDisabled && !isActive ? 'opacity-50 cursor-default' : ''
            } ${
              isActive
                ? 'bg-[var(--theme-primary)] text-themed-button shadow-[0_2px_4px_color-mix(in_srgb,var(--theme-primary)_25%,transparent)]'
                : 'bg-transparent text-themed-muted'
            } ${isDisabled ? 'cursor-default' : 'cursor-pointer'}`}
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

        return option.tooltip ? (
          <Tooltip key={option.value} content={option.tooltip} strategy="overlay">
            {buttonElement}
          </Tooltip>
        ) : (
          <React.Fragment key={option.value}>{buttonElement}</React.Fragment>
        );
      })}
    </div>
  );
};

export default SegmentedControl;
