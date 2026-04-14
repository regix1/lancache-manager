import React, { useEffect, useState } from 'react';

interface HighlightGlowProps {
  children: React.ReactNode;
  enabled?: boolean;
  color?: string;
  duration?: number;
  className?: string;
  /**
   * `navigate` (default): 2-pulse attention-grab used when jumping to a single
   * target. `subtle`: a single, quicker, smaller-radius pulse used for bulk
   * acknowledgements (e.g. Reset to Defaults flashing every card at once).
   */
  variant?: 'navigate' | 'subtle';
}

const DEFAULT_DURATION = 2000;
const SUBTLE_DEFAULT_DURATION = 1400;

/**
 * Renders an animated glow effect around content.
 * Applies the glow animation directly to a wrapper element, avoiding portal/positioning complexity.
 */
const HighlightGlow: React.FC<HighlightGlowProps> = ({
  children,
  enabled = false,
  color = 'var(--theme-primary)',
  duration,
  className,
  variant = 'navigate'
}) => {
  const resolvedDuration =
    duration ?? (variant === 'subtle' ? SUBTLE_DEFAULT_DURATION : DEFAULT_DURATION);
  const [isAnimating, setIsAnimating] = useState(false);

  useEffect(() => {
    if (!enabled) {
      setIsAnimating(false);
      return;
    }

    setIsAnimating(true);

    const timer = window.setTimeout(() => {
      setIsAnimating(false);
    }, resolvedDuration);

    return () => {
      window.clearTimeout(timer);
    };
  }, [enabled, resolvedDuration]);

  const variantClass = variant === 'subtle' ? 'highlight-glow-subtle' : '';

  return (
    <div
      className={`highlight-glow-wrapper ${variantClass} ${isAnimating ? 'highlight-glow-active' : ''} ${className || ''}`}
      style={
        {
          '--glow-color': color,
          '--glow-duration': `${resolvedDuration}ms`
        } as React.CSSProperties
      }
    >
      {children}
    </div>
  );
};

export default HighlightGlow;
