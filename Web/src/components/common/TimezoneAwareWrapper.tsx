import React from 'react';

/**
 * Wrapper component for timezone-aware content.
 * Previously used key-based remounting which caused flash/flicker.
 * Now just passes children through - useFormattedDateTime hook handles updates
 * via refreshKey dependency in its useMemo.
 */
export const TimezoneAwareWrapper: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  return <>{children}</>;
};
