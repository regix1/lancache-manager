import React, { type ReactNode } from 'react';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';
import { DirectoryPermissionsContext } from './DirectoryPermissionsContext.types';

interface DirectoryPermissionsProviderProps {
  children: ReactNode;
}

export const DirectoryPermissionsProvider: React.FC<DirectoryPermissionsProviderProps> = ({
  children
}) => {
  const permissions = useDirectoryPermissions();

  return (
    <DirectoryPermissionsContext.Provider value={permissions}>
      {children}
    </DirectoryPermissionsContext.Provider>
  );
};
