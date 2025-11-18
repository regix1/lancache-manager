import React from 'react';
import { useTimezone } from '@contexts/TimezoneContext';

/**
 * Wrapper component that forces all children to re-render when timezone preference changes
 * Uses React's key prop to unmount/remount the entire tree
 */
export const TimezoneAwareWrapper: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { refreshKey } = useTimezone();

  // When refreshKey changes, React will unmount and remount all children
  // This ensures all displayed times update to the new timezone
  return <React.Fragment key={`timezone-${refreshKey}`}>{children}</React.Fragment>;
};
