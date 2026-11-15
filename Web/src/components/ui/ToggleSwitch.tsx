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
  /** Explain what the toggle DOES. Repeating the label beside it only covers that label. */
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

  // Several callers move `value` only once the server has answered, so the half the user clicked
  // is held and drawn active for as long as the save runs; once it ends, `value` decides again,
  // which puts a failed save back where it was.
  const [clickedValue, setClickedValue] = React.useState<string | null>(null);
  React.useEffect(() => {
    if (!loading) setClickedValue(null);
  }, [loading]);
  const shownValue = loading && clickedValue !== null ? clickedValue : value;

  // w-fit below: the pill is sized by its two labels, never by the column it sits in. As a plain
  // child of a stretching flex column it would otherwise run the full width of the form and read as
  // a banner rather than a control. It used to be spared that by the tooltip wrapper it no longer
  // always has.
  // While it saves the switch keeps its size and brightness: the spinner is laid over the active
  // half's own label, which stays in place (and stays its accessible name) but does not paint.
  const button = (
    <button
      onClick={() => {
        const currentIndex = options.findIndex((o) => o.value === value);
        const nextIndex = currentIndex === 0 ? 1 : 0;
        setClickedValue(options[nextIndex].value);
        onChange(options[nextIndex].value);
      }}
      disabled={disabled || loading}
      className={`flex w-fit items-center rounded-full font-medium transition bg-themed-secondary ${sizeClasses[size]} ${
        disabled ? 'opacity-60 cursor-wait' : loading ? 'cursor-wait' : 'cursor-pointer'
      }`}
    >
      {options.map((option) => {
        const isActive = shownValue === option.value;
        const busy = loading && isActive;
        return (
          <span
            key={option.value}
            className={`flex items-center justify-center gap-1.5 ${textClasses[size]} h-full rounded-full transition ${
              isActive ? 'shadow-sm no-dark-shadow' : 'text-themed-muted'
            } ${busy ? 'relative' : ''}`}
            style={getActiveStyles(option, isActive)}
          >
            <span className={`contents ${busy ? 'btn-busy-content' : ''}`}>
              {option.icon && React.isValidElement(option.icon)
                ? React.cloneElement(option.icon as React.ReactElement<{ className?: string }>, {
                    className: iconSizes[size]
                  })
                : option.icon}
              {option.label}
            </span>
            {busy && (
              <span className="absolute inset-0 flex items-center justify-center">
                <LoadingSpinner inline size={size === 'sm' ? 'xs' : 'sm'} />
              </span>
            )}
          </span>
        );
      })}
    </button>
  );

  if (title) {
    // Below the control, not above it. These toggles sit directly under (or beside) their own
    // label, and a tooltip placed on top lands on that label and hides the words the reader was
    // reaching for. EdgeTooltip still flips back to the top when there is no room underneath.
    return (
      <Tooltip content={title} position="bottom">
        {button}
      </Tooltip>
    );
  }

  return button;
};
