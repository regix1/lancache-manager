import React from 'react';
import { Tooltip } from '@components/ui/Tooltip';

interface NginxReopenActionGateProps {
  available: boolean;
  tooltip: string;
  position?: 'top' | 'bottom' | 'left' | 'right';
  className?: string;
  children: React.ReactNode;
}

export const NginxReopenActionGate: React.FC<NginxReopenActionGateProps> = ({
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
