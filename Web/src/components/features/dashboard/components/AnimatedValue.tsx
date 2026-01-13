import React, { useMemo } from 'react';
import NumberFlow from '@number-flow/react';

interface AnimatedValueProps {
  /** The value to display - can be a number or formatted string like "1.5 GB" */
  value: string | number;
  /** Additional CSS classes */
  className?: string;
  /** Whether to animate the value (default: true) */
  animate?: boolean;
}

/**
 * Parses a formatted string like "1.5 GB" into { number: 1.5, suffix: " GB" }
 * Returns isTextOnly: true when the value is pure text (no leading number)
 */
const parseFormattedValue = (value: string | number): { number: number; suffix: string; decimals: number; isTextOnly: boolean } => {
  if (typeof value === 'number') {
    return { number: value, suffix: '', decimals: 0, isTextOnly: false };
  }

  // Match number at the start, possibly with decimals, and capture the rest as suffix
  const match = value.match(/^([\d,.]+)\s*(.*)$/);
  if (match) {
    // Remove commas and parse the number
    const numStr = match[1].replace(/,/g, '');
    const num = parseFloat(numStr);
    const suffix = match[2] ? ` ${match[2]}` : '';

    // Count decimal places
    const decimalIndex = numStr.indexOf('.');
    const decimals = decimalIndex >= 0 ? numStr.length - decimalIndex - 1 : 0;

    if (!isNaN(num)) {
      return { number: num, suffix, decimals, isTextOnly: false };
    }
  }

  // If parsing fails, mark as text-only (don't show 0 prefix)
  return { number: 0, suffix: value, decimals: 0, isTextOnly: true };
};

/**
 * Component that displays a value with smooth digit-spinning animation
 * Uses NumberFlow for buttery smooth transitions
 */
const AnimatedValue: React.FC<AnimatedValueProps> = ({
  value,
  className = '',
  animate = true,
}) => {
  const parsed = useMemo(() => parseFormattedValue(value), [value]);

  // If animation is disabled, just show the raw value
  if (!animate) {
    return (
      <span className={`${className} tabular-nums text-themed-primary`}>
        {typeof value === 'string' ? value : value.toLocaleString()}
      </span>
    );
  }

  // For text-only values (like "Disabled" or "No data"), just display the text
  if (parsed.isTextOnly) {
    return (
      <span className={`${className} tabular-nums text-themed-primary`}>
        {parsed.suffix}
      </span>
    );
  }

  return (
    <span className={`${className} tabular-nums text-themed-primary inline-flex items-baseline`}>

      <NumberFlow
        value={parsed.number}
        format={{
          minimumFractionDigits: parsed.decimals,
          maximumFractionDigits: parsed.decimals,
        }}
        transformTiming={{
          duration: 700,
          easing: 'cubic-bezier(0.16, 1, 0.3, 1)' // Smooth ease-out expo
        }}
        spinTiming={{
          duration: 700,
          easing: 'cubic-bezier(0.16, 1, 0.3, 1)'
        }}
        opacityTiming={{
          duration: 450,
          easing: 'ease-out'
        }}
        willChange
      />
      {parsed.suffix && <span>{parsed.suffix}</span>}
    </span>
  );
};

export default AnimatedValue;
