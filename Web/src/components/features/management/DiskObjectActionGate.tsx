import React from 'react';
import { Tooltip } from '@components/ui/Tooltip';

interface DiskObjectActionGateProps {
  /** Whether disk-level object operations are available across the fleet. */
  available: boolean;
  /** Explanation shown on hover while the wrapped action is unavailable. */
  tooltip: string;
  position?: 'top' | 'bottom' | 'left' | 'right';
  /** Trigger wrapper class. Full-width menu items pass "block w-full"; inline buttons keep the default. */
  className?: string;
  children: React.ReactNode;
}

/**
 * Wraps a disk-level object action (per-game/service cache removal, corruption detection and
 * removal, eviction removal). When the fleet cannot map logical objects on disk, the caller
 * disables the control and this adds a hover explanation. When available, the child renders
 * untouched so normal controls gain no extra tooltip.
 */
export const DiskObjectActionGate: React.FC<DiskObjectActionGateProps> = ({
  available,
  tooltip,
  position = 'top',
  className = 'inline-flex',
  children
}) => {
  if (available) {
    return <>{children}</>;
  }
  return (
    <Tooltip content={tooltip} position={position} className={className}>
      {children}
    </Tooltip>
  );
};
