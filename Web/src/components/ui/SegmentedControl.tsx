import React from 'react';
import { Tooltip } from './Tooltip';

interface SegmentedControlOption {
  value: string;
  label?: React.ReactNode;
  icon?: React.ReactNode;
  tooltip?: string;
  disabled?: boolean;
}

interface SegmentedControlProps {
  options: SegmentedControlOption[];
  value: string;
  onChange: (value: string) => void;
  /** Fill for whichever segment is selected. One control, one accent. */
  activeColor?: 'primary' | 'warning' | 'neutral';
  size?: 'sm' | 'md';
  // true, false or 'responsive' (labels from lg up)
  showLabels?: boolean | 'responsive';
  fullWidth?: boolean;
  className?: string;
}

export const SegmentedControl: React.FC<SegmentedControlProps> = ({
  options,
  value,
  onChange,
  activeColor = 'primary',
  size = 'md',
  showLabels = false,
  fullWidth = false,
  className = ''
}) => {
  const sizeClasses = {
    sm: {
      // Explicit container height (32px) that the buttons fill via flex stretch, instead of
      // sizing the container off a button min-height built from stacked line-height + padding
      // + border layers. That composed height happened to equal 32px too, but each layer
      // rounds to the nearest device pixel independently, so at non-100% OS/browser zoom the
      // container and an adjacent fixed-height control (e.g. the toggle button next to it)
      // could round to different pixel heights and appear misaligned by a hair. A single
      // explicit height has nothing left to round differently.
      container: 'p-[3px] h-8',
      button: 'px-2 py-1',
      icon: 14,
      text: 'text-xs'
    },
    md: {
      container: 'p-[3px] h-10',
      button: 'px-3 py-[6px]',
      icon: 14,
      text: 'text-sm'
    }
  };

  const sizes = sizeClasses[size];

  const activeClass =
    activeColor === 'warning'
      ? 'bg-[var(--theme-warning)] text-themed-button'
      : activeColor === 'neutral'
        ? 'bg-[var(--theme-selected-bg)] text-[var(--theme-selected-text)]'
        : 'bg-[var(--theme-primary)] text-themed-button';

  return (
    // radiogroup, not group: a couple of callers already wrap this control in their own
    // role="group" for a label, and a mutually-exclusive picker is a radiogroup/radio pair
    // regardless, so this never doubles up with a caller's wrapper. [40]
    <div
      role="radiogroup"
      className={`inline-flex segmented-control-container ${sizes.container} ${fullWidth ? 'w-full' : ''} ${className} bg-themed-tertiary border border-themed-secondary`}
    >
      {options.map((option) => {
        const isActive = value === option.value;
        const isDisabled = option.disabled;
        // Disabled + selected: drop the vivid accent for a neutral muted fill so the whole control
        // reads as disabled (matching a disabled dropdown/toggle) instead of looking clickable.
        const segmentClass = isActive
          ? isDisabled
            ? 'bg-themed-surface-active text-themed-muted'
            : activeClass
          : 'bg-transparent text-themed-muted';
        // An icon-only segment renders no label span, so the button has no accessible name and a
        // screen reader announces a bare radio. Name it from the text the option already carries,
        // and only when no label is rendered - an aria-label over visible text would override it.
        const rendersLabel = Boolean(option.label) && (showLabels !== false || !option.icon);
        const segmentName = rendersLabel
          ? undefined
          : (option.tooltip ?? (typeof option.label === 'string' ? option.label : undefined));

        const buttonElement = (
          <button
            key={option.value}
            // Without this the button defaults to type="submit", so picking a segment inside a
            // form submits it instead of just changing the selection.
            type="button"
            role="radio"
            aria-checked={isActive}
            aria-label={segmentName}
            onClick={() => !isDisabled && onChange(option.value)}
            disabled={isDisabled}
            className={`segmented-control-button ${sizes.button} transition flex items-center justify-center gap-[0.5rem] font-semibold whitespace-nowrap text-xs ${
              fullWidth ? 'flex-1' : ''
            } ${segmentClass} ${isDisabled ? 'opacity-60 cursor-default' : 'cursor-pointer'}`}
          >
            {option.icon && React.isValidElement(option.icon)
              ? React.cloneElement(option.icon as React.ReactElement<{ size?: number }>, {
                  size: sizes.icon
                })
              : option.icon}
            {/* One span for all three modes - the three that used to sit here differed only in
                when they rendered, and a class added to one of them never reached the others.
                .segmented-control-label is what spaces a label from a count badge riding beside
                it; a plain-string label has no second item for the gap to act on. */}
            {rendersLabel && (
              <span
                className={`segmented-control-label ${sizes.text}${
                  showLabels === 'responsive' ? ' hidden lg:inline-flex' : ''
                }`}
              >
                {option.label}
              </span>
            )}
          </button>
        );

        // The tooltip wrapper (not the button) becomes the container's flex item, so
        // width-splitting styles (e.g. the segment-uniform recipes) must be able to
        // target it - hence the stable segmented-control-slot class. For the same reason
        // the wrapper carries the width split under fullWidth: flex-1 on the inner button
        // only divides the wrapper, which would otherwise stay sized to its own text and
        // leave the segments bunched at one end of a full-width container.
        return option.tooltip ? (
          <Tooltip
            key={option.value}
            content={option.tooltip}
            strategy="overlay"
            className={`inline-flex segmented-control-slot ${fullWidth ? 'flex-1' : ''}`}
          >
            {buttonElement}
          </Tooltip>
        ) : (
          <React.Fragment key={option.value}>{buttonElement}</React.Fragment>
        );
      })}
    </div>
  );
};
