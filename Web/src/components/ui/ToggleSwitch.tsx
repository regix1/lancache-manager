import React from 'react';
import { Loader2 } from 'lucide-react';
import { Tooltip } from './Tooltip';

interface ToggleOption {
  value: string;
  label: string;
  icon?: React.ReactNode;
  activeColor?: 'success' | 'error' | 'warning' | 'info' | 'default';
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
        backgroundColor: 'color-mix(in srgb, var(--theme-success) 20%, transparent)',
        color: 'var(--theme-success-text)'
      },
      error: {
        backgroundColor: 'color-mix(in srgb, var(--theme-error) 20%, transparent)',
        color: 'var(--theme-error-text)'
      },
      warning: {
        backgroundColor: 'color-mix(in srgb, var(--theme-warning) 20%, transparent)',
        color: 'var(--theme-warning-text)'
      },
      info: {
        backgroundColor: 'color-mix(in srgb, var(--theme-info) 20%, transparent)',
        color: 'var(--theme-info-text)'
      },
      default: {
        backgroundColor: 'var(--theme-bg-tertiary)',
        color: 'var(--theme-text-primary)'
      }
    };

    return colorMap[option.activeColor || 'default'];
  };

  const sizeClasses = {
    sm: 'px-3 py-1.5 text-xs',
    md: 'px-4 py-2 text-sm'
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
      className={`flex items-center rounded-full font-medium transition-all bg-themed-secondary ${
        disabled || loading ? 'opacity-60 cursor-wait' : 'cursor-pointer'
      }`}
    >
      {options.map((option) => {
        const isActive = value === option.value;
        return (
          <span
            key={option.value}
            className={`flex items-center gap-1.5 ${sizeClasses[size]} rounded-full transition-all ${
              isActive ? 'shadow-sm' : 'text-themed-muted'
            }`}
            style={getActiveStyles(option, isActive)}
          >
            {loading && isActive ? (
              <Loader2 className={`${iconSizes[size]} animate-spin`} />
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
