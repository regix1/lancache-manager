import React, { useMemo } from 'react';
import { useAnimatedNumber } from '../hooks/useAnimatedNumber';

interface AnimatedValueProps {
  /** The value to display - can be a number or formatted string like "1.5 GB" */
  value: string | number;
  /** Additional CSS classes */
  className?: string;
  /** Whether to animate the value (default: true) */
  animate?: boolean;
  /** Animation duration in ms (default: 800) */
  duration?: number;
  /** Custom formatter for the animated number */
  formatter?: (value: number) => string;
}

/**
 * Parses a formatted string like "1.5 GB" into { number: 1.5, suffix: " GB" }
 */
const parseFormattedValue = (value: string | number): { number: number; suffix: string } => {
  if (typeof value === 'number') {
    return { number: value, suffix: '' };
  }

  // Match number at the start, possibly with decimals, and capture the rest as suffix
  const match = value.match(/^([\d,.]+)\s*(.*)$/);
  if (match) {
    // Remove commas and parse the number
    const numStr = match[1].replace(/,/g, '');
    const num = parseFloat(numStr);
    const suffix = match[2] ? ` ${match[2]}` : '';

    if (!isNaN(num)) {
      return { number: num, suffix };
    }
  }

  // If parsing fails, return 0 with the original value as suffix
  return { number: 0, suffix: value };
};

/**
 * Formats a number with commas for thousands
 */
const formatWithCommas = (num: number, decimals: number): string => {
  return num.toLocaleString('en-US', {
    minimumFractionDigits: decimals,
    maximumFractionDigits: decimals,
  });
};

/**
 * Component that displays a value with a counting animation
 */
const AnimatedValue: React.FC<AnimatedValueProps> = ({
  value,
  className = '',
  animate = true,
  duration = 800,
  formatter,
}) => {
  const parsed = useMemo(() => parseFormattedValue(value), [value]);

  // Track previous suffix to detect unit changes (e.g., GB → TB)
  const prevSuffixRef = React.useRef(parsed.suffix);
  const suffixChanged = prevSuffixRef.current !== parsed.suffix;

  // Update the ref after checking
  React.useEffect(() => {
    prevSuffixRef.current = parsed.suffix;
  }, [parsed.suffix]);

  // Determine decimal places from the original number
  const decimals = useMemo(() => {
    const str = parsed.number.toString();
    const decimalIndex = str.indexOf('.');
    return decimalIndex >= 0 ? str.length - decimalIndex - 1 : 0;
  }, [parsed.number]);

  // Don't animate when the unit/suffix changes (e.g., switching from GB to TB)
  // This prevents showing nonsensical values like "537 TB" when switching time ranges
  const shouldAnimate = animate && !suffixChanged;

  const { displayValue, isAnimating } = useAnimatedNumber({
    value: parsed.number,
    duration,
    enabled: shouldAnimate,
    decimals,
    easing: 'smooth',
  });

  const displayString = useMemo(() => {
    if (formatter) {
      return formatter(displayValue);
    }
    return formatWithCommas(displayValue, decimals) + parsed.suffix;
  }, [displayValue, decimals, parsed.suffix, formatter]);

  return (
    <span
      className={`${className} ${isAnimating ? 'animate-count' : ''}`}
      style={{ fontVariantNumeric: 'tabular-nums' }}
    >
      {displayString}
    </span>
  );
};

export default AnimatedValue;
