import React from 'react';

interface WidgetPanelProps {
  children: React.ReactNode;
  /** Frosted-glass surface variant; toggled by the glassmorphism setting at 2 of the 6 sites. */
  glass?: boolean;
  /** Extra classes for a panel-specific layout, e.g. BandwidthTrend's wide variant. */
  className?: string;
}

// Dashboard widget shell. Wraps .widget-card (styles/components/cards.css:93), which now carries
// the same background-clip, transition and light-theme shadow rules as .themed-card so a widget
// panel matches a Card panel in the same grid.
export const WidgetPanel: React.FC<WidgetPanelProps> = ({
  children,
  glass = false,
  className = ''
}) => (
  <div className={`widget-card${glass ? ' glass' : ''}${className ? ` ${className}` : ''}`}>
    {children}
  </div>
);
