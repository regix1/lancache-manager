import React, { useEffect, useState } from 'react';

interface HighlightGlowProps {
  children: React.ReactNode;
  enabled?: boolean;
  color?: string;
  duration?: number;
  className?: string;
}

const DEFAULT_DURATION = 2000;

/**
 * Renders an animated glow effect around content.
 * Applies the glow animation directly to a wrapper element, avoiding portal/positioning complexity.
 */
const HighlightGlow: React.FC<HighlightGlowProps> = ({
  children,
  enabled = false,
  color = 'var(--theme-primary)',
  duration = DEFAULT_DURATION,
  className
}) => {
  const [isAnimating, setIsAnimating] = useState(false);

  useEffect(() => {
    if (!enabled) {
      setIsAnimating(false);
      return;
    }

    setIsAnimating(true);

    const timer = window.setTimeout(() => {
      setIsAnimating(false);
    }, duration);

    return () => {
      window.clearTimeout(timer);
    };
  }, [enabled, duration]);

  return (
    <div
      className={`highlight-glow-wrapper ${isAnimating ? 'highlight-glow-active' : ''} ${className || ''}`}
      style={
        {
          '--glow-color': color,
          '--glow-duration': `${duration}ms`
        } as React.CSSProperties
      }
    >
      {children}
    </div>
  );
};

export default HighlightGlow;
