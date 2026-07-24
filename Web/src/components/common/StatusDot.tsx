import React from 'react';

interface StatusDotProps {
  /** 'active' = green pulse, 'away' = amber pulse, 'inactive' = muted. */
  state: 'active' | 'away' | 'inactive';
  /** Accessible label describing what the dot represents (e.g. "Running", "Idle"). */
  label: string;
  className?: string;
}

/**
 * Shared status/presence dot. Reuses the app-wide `.status-dot` base (styles/utilities/patterns.css)
 * so every "running/active" indicator looks and animates identically. The state is supplied by the
 * caller, typically from useActivityStatus, so a single event drives dots across the whole app.
 */
const StatusDot: React.FC<StatusDotProps> = ({ state, label, className }) => (
  <span
    className={`status-dot ${state}${className ? ` ${className}` : ''}`}
    role="img"
    aria-label={label}
  />
);

export default StatusDot;
