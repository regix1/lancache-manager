import React from 'react';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { Tooltip } from './Tooltip';

interface ToggleOption {
  value: string;
  label: string;
  icon?: React.ReactNode;
  activeColor?: 'success' | 'error' | 'warning' | 'info' | 'waiting' | 'default';
}

interface ToggleSwitchProps {
  options: [ToggleOption, ToggleOption];
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  loading?: boolean;
  title?: string;
  size?: 'sm' | 'md';
}

export const ToggleSwitch: React.FC<ToggleSwitchProps> = ({
  options,
  value,
  onChange,
  disabled = false,
  loading = false,
  title,
  size = 'sm'
}) => {
  const getActiveStyles = (option: ToggleOption, isActive: boolean) => {
    if (!isActive) return {};

    const colorMap = {
      success: {
        backgroundColor: 'var(--theme-success-muted)',
        color: 'var(--theme-success-text)'
      },
      error: {
        backgroundColor: 'var(--theme-error-muted)',
        color: 'var(--theme-error-text)'
      },
      warning: {
        backgroundColor: 'var(--theme-warning-muted)',
        color: 'var(--theme-warning-text)'
      },
      info: {
        backgroundColor: 'var(--theme-info-muted)',
        color: 'var(--theme-info-text)'
      },
      waiting: {
        backgroundColor: 'var(--theme-waiting-muted)',
        color: 'var(--theme-waiting-text)'
      },
      default: {
        backgroundColor: 'var(--theme-bg-surface-active)',
        color: 'var(--theme-text-primary)'
      }
    };

    return colorMap[option.activeColor || 'default'];
  };

  // Explicit height (matches Button's same-named size: sm=32px/h-8, md=40px/h-10) that the
  // option spans fill via flex stretch, instead of sizing the button off each span's own
  // padding + line-height. A composed height can round to a different device pixel than an
  // adjacent fixed-height Button of the "same" size at non-100% OS/browser zoom - see the
  // identical fix on SegmentedControl.
  const sizeClasses = {
    sm: 'h-8',
    md: 'h-10'
  };

  const textClasses = {
    sm: 'px-3 text-xs',
    md: 'px-4 text-sm'
  };

  const iconSizes = {
    sm: 'w-3.5 h-3.5',
    md: 'w-4 h-4'
  };

  const button = (
    <button
      onClick={() => {
        const currentIndex = options.findIndex((o) => o.value === value);
        const nextIndex = currentIndex === 0 ? 1 : 0;
        onChange(options[nextIndex].value);
      }}
      disabled={disabled || loading}
      className={`flex items-center rounded-full font-medium transition bg-themed-secondary ${sizeClasses[size]} ${
        disabled || loading ? 'opacity-60 cursor-wait' : 'cursor-pointer'
      }`}
    >
      {options.map((option) => {
        const isActive = value === option.value;
        return (
          <span
            key={option.value}
            className={`flex items-center justify-center gap-1.5 ${textClasses[size]} h-full rounded-full transition ${
              isActive ? 'shadow-sm' : 'text-themed-muted'
            }`}
            style={getActiveStyles(option, isActive)}
          >
            {loading && isActive ? (
              <LoadingSpinner inline size={size === 'sm' ? 'xs' : 'sm'} />
            ) : option.icon && React.isValidElement(option.icon) ? (
              React.cloneElement(option.icon as React.ReactElement<{ className?: string }>, {
                className: iconSizes[size]
              })
            ) : (
              option.icon
            )}
            {option.label}
          </span>
        );
      })}
    </button>
  );

  if (title) {
    return (
      <Tooltip content={title} position="top">
        {button}
      </Tooltip>
    );
  }

  return button;
};
