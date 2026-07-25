import { useContext } from 'react';
import { DirectoryPermissionsContext } from './DirectoryPermissionsContext.types';
import { createContextHook } from './createContextHook';

export const useDirectoryPermissionsContext = createContextHook(
  DirectoryPermissionsContext,
  'useDirectoryPermissionsContext'
);

/** Returns undefined outside DirectoryPermissionsProvider (e.g. Button with awaitPermissions). */
export const useOptionalDirectoryPermissionsContext = () => useContext(DirectoryPermissionsContext);
