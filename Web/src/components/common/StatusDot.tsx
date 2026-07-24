import React from 'react';

interface StatusDotProps {
  /**
   * Live/pulsing presence state - 'active' = green pulse, 'away' = amber pulse, 'inactive' = muted.
   * Mutually exclusive with `tone`.
   */
  state?: 'active' | 'away' | 'inactive';
  /**
   * Static (non-pulsing) severity reading for a point-in-time status, not live presence - e.g. a
   * container's current running/idle/warning state. 'running' and 'info' deliberately do NOT reuse
   * `state`'s 'active': that value pulses and always means success (green), while these tones are
   * static and 'info' means "in progress" (blue), not success. Mutually exclusive with `state`.
   */
  tone?: 'idle' | 'running' | 'warning' | 'error' | 'info';
  /** Accessible label describing what the dot represents (e.g. "Running", "Idle"). */
  label: string;
  className?: string;
}

/**
 * Shared status/presence dot. Reuses the app-wide `.status-dot` base (styles/utilities/patterns.css)
 * so every "running/active" indicator looks and animates identically. Exactly one of `state`/`tone`
 * is supplied by the caller, typically `state` from useActivityStatus for live presence, or `tone` for
 * a static severity reading - either way, a single set of dot styles drives the whole app.
 */
const StatusDot: React.FC<StatusDotProps> = ({ state, tone, label, className }) => (
  <span
    className={`status-dot ${state ?? tone ?? ''}${className ? ` ${className}` : ''}`}
    role="img"
    aria-label={label}
  />
);

export default StatusDot;
