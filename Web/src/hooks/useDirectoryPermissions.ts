import { useState, useEffect, useCallback } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext';

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
 * Auto-refreshes when DirectoryPermissionsChanged SignalR event is received.
 */
export const useDirectoryPermissions = (): DirectoryPermissions => {
  const { on, off } = useSignalR();
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

  // Auto-refresh when backend detects permission changes
  const handlePermissionsChanged = useCallback(() => {
    loadDirectoryPermissions();
  }, [loadDirectoryPermissions]);

  useEffect(() => {
    loadDirectoryPermissions();

    on('DirectoryPermissionsChanged', handlePermissionsChanged);

    return () => {
      off('DirectoryPermissionsChanged', handlePermissionsChanged);
    };
  }, [loadDirectoryPermissions, on, off, handlePermissionsChanged]);

  return {
    logsReadOnly,
    cacheReadOnly,
    logsExist,
    cacheExist,
    checkingPermissions,
    reload: loadDirectoryPermissions
  };
};
