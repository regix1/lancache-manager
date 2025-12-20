import { useState, useEffect, useRef, useCallback } from 'react';

interface UseAnimatedNumberOptions {
  /** The target value to animate to */
  value: number;
  /** Animation duration in milliseconds (default: 600) */
  duration?: number;
  /** Easing function type */
  easing?: 'linear' | 'easeOut' | 'easeInOut' | 'smooth';
  /** Number of decimal places to show */
  decimals?: number;
  /** Whether to animate (respects prefers-reduced-motion) */
  enabled?: boolean;
}

interface UseAnimatedNumberResult {
  /** The current animated value */
  displayValue: number;
  /** Whether the animation is currently running */
  isAnimating: boolean;
}

// Easing functions - using smoother curves for number animations
const easingFunctions = {
  linear: (t: number) => t,
  // Quadratic ease out - gentler deceleration than cubic
  easeOut: (t: number) => 1 - Math.pow(1 - t, 2),
  // Smooth ease in-out with gradual start and end
  easeInOut: (t: number) => t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2,
  // Extra smooth for large value changes - sine-based
  smooth: (t: number) => -(Math.cos(Math.PI * t) - 1) / 2,
};

/**
 * Hook for animating number values with smooth counting effect
 */
export const useAnimatedNumber = ({
  value,
  duration = 600,
  easing = 'smooth',
  decimals = 0,
  enabled = true,
}: UseAnimatedNumberOptions): UseAnimatedNumberResult => {
  const [displayValue, setDisplayValue] = useState(value);
  const [isAnimating, setIsAnimating] = useState(false);
  const animationRef = useRef<number | null>(null);
  const startValueRef = useRef(value);
  const startTimeRef = useRef<number | null>(null);
  const previousValueRef = useRef(value);

  // Check for reduced motion preference
  const prefersReducedMotion = useCallback(() => {
    if (typeof window === 'undefined') return true;
    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }, []);

  useEffect(() => {
    // Skip animation if disabled or user prefers reduced motion
    if (!enabled || prefersReducedMotion()) {
      setDisplayValue(value);
      return;
    }

    // Skip if value hasn't changed
    if (value === previousValueRef.current) {
      return;
    }

    // Cancel any existing animation
    if (animationRef.current !== null) {
      cancelAnimationFrame(animationRef.current);
    }

    startValueRef.current = displayValue;
    startTimeRef.current = null;
    previousValueRef.current = value;
    setIsAnimating(true);

    const easingFn = easingFunctions[easing];
    const targetValue = value;
    const startValue = startValueRef.current;
    const valueDiff = targetValue - startValue;

    const animate = (currentTime: number) => {
      if (startTimeRef.current === null) {
        startTimeRef.current = currentTime;
      }

      const elapsed = currentTime - startTimeRef.current;
      const progress = Math.min(elapsed / duration, 1);
      const easedProgress = easingFn(progress);

      const currentValue = startValue + valueDiff * easedProgress;
      const roundedValue = Number(currentValue.toFixed(decimals));

      setDisplayValue(roundedValue);

      if (progress < 1) {
        animationRef.current = requestAnimationFrame(animate);
      } else {
        setDisplayValue(Number(targetValue.toFixed(decimals)));
        setIsAnimating(false);
        animationRef.current = null;
      }
    };

    animationRef.current = requestAnimationFrame(animate);

    return () => {
      if (animationRef.current !== null) {
        cancelAnimationFrame(animationRef.current);
      }
    };
  }, [value, duration, easing, decimals, enabled, prefersReducedMotion, displayValue]);

  // Initialize with correct value
  useEffect(() => {
    if (!enabled || prefersReducedMotion()) {
      setDisplayValue(value);
    }
  }, [enabled, prefersReducedMotion, value]);

  return { displayValue, isAnimating };
};

export default useAnimatedNumber;
