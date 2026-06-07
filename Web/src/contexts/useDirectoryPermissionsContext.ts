import { useContext } from 'react';
import { DirectoryPermissionsContext } from './DirectoryPermissionsContext.types';

export const useDirectoryPermissionsContext = () => {
  const context = useContext(DirectoryPermissionsContext);
  if (!context) {
    throw new Error(
      'useDirectoryPermissionsContext must be used within DirectoryPermissionsProvider'
    );
  }
  return context;
};

/** Returns undefined outside DirectoryPermissionsProvider (e.g. Button with awaitPermissions). */
export const useOptionalDirectoryPermissionsContext = () => useContext(DirectoryPermissionsContext);
