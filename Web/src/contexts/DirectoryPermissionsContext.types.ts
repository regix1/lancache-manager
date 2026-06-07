import { createContext } from 'react';

export interface DirectoryPermissions {
  logsReadOnly: boolean;
  cacheReadOnly: boolean;
  logsExist: boolean;
  cacheExist: boolean;
  cacheWritable: boolean;
  logsWritable: boolean;
  cachePath: string;
  logsPath: string;
  dockerSocketAvailable: boolean;
  checkingPermissions: boolean;
  timedOut: boolean;
  error: string | null;
  reload: () => Promise<void>;
}

export const DirectoryPermissionsContext = createContext<DirectoryPermissions | undefined>(
  undefined
);
