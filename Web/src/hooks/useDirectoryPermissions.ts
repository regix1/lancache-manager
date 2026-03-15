import { useState, useEffect, useCallback } from 'react';
import ApiService from '@services/api.service';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';

interface DirectoryPermissions {
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
  error: string | null;
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
  const [cacheWritable, setCacheWritable] = useState(false);
  const [logsWritable, setLogsWritable] = useState(false);
  const [cachePath, setCachePath] = useState('');
  const [logsPath, setLogsPath] = useState('');
  const [dockerSocketAvailable, setDockerSocketAvailable] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const loadDirectoryPermissions = useCallback(async () => {
    try {
      setCheckingPermissions(true);
      setError(null);
      const data = await ApiService.getDirectoryPermissions();
      setLogsReadOnly(data.logs.readOnly);
      setCacheReadOnly(data.cache.readOnly);
      setLogsExist(data.logs.exists);
      setCacheExist(data.cache.exists);
      setCacheWritable(data.cache.writable);
      setLogsWritable(data.logs.writable);
      setCachePath(data.cache.path);
      setLogsPath(data.logs.path);
      setDockerSocketAvailable(data.dockerSocket.available);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setError('Failed to check permissions');
      // Fallback values on error
      setLogsReadOnly(false);
      setCacheReadOnly(false);
      setLogsExist(true);
      setCacheExist(true);
      setCacheWritable(false);
      setLogsWritable(false);
      setCachePath('');
      setLogsPath('');
      setDockerSocketAvailable(false);
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
    cacheWritable,
    logsWritable,
    cachePath,
    logsPath,
    dockerSocketAvailable,
    checkingPermissions,
    error,
    reload: loadDirectoryPermissions
  };
};
