import { useState, useEffect, useCallback } from 'react';
import ApiService from '@services/api.service';

interface DirectoryPermissions {
  logsReadOnly: boolean;
  cacheReadOnly: boolean;
  logsExist: boolean;
  cacheExist: boolean;
  checkingPermissions: boolean;
  reload: () => Promise<void>;
}

/**
 * Hook to check directory permissions for logs and cache directories.
 * Calls ApiService.getDirectoryPermissions() on mount and provides a reload function.
 */
export const useDirectoryPermissions = (): DirectoryPermissions => {
  const [logsReadOnly, setLogsReadOnly] = useState(false);
  const [cacheReadOnly, setCacheReadOnly] = useState(false);
  const [logsExist, setLogsExist] = useState(true);
  const [cacheExist, setCacheExist] = useState(true);
  const [checkingPermissions, setCheckingPermissions] = useState(true);

  const loadDirectoryPermissions = useCallback(async () => {
    try {
      setCheckingPermissions(true);
      const data = await ApiService.getDirectoryPermissions();
      setLogsReadOnly(data.logs.readOnly);
      setCacheReadOnly(data.cache.readOnly);
      setLogsExist(data.logs.exists);
      setCacheExist(data.cache.exists);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      // Fallback values on error
      setLogsReadOnly(false);
      setCacheReadOnly(false);
      setLogsExist(true);
      setCacheExist(true);
    } finally {
      setCheckingPermissions(false);
    }
  }, []);

  useEffect(() => {
    loadDirectoryPermissions();
  }, [loadDirectoryPermissions]);

  return {
    logsReadOnly,
    cacheReadOnly,
    logsExist,
    cacheExist,
    checkingPermissions,
    reload: loadDirectoryPermissions
  };
};
