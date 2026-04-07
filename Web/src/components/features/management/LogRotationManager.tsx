import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { useDockerSocket } from '@contexts/useDockerSocket';
import { useNotifications } from '@contexts/notifications';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { formatDateTime } from '@utils/formatters';
import { RefreshCw, Clock, CheckCircle2, XCircle } from 'lucide-react';
import { LoadingState } from '@components/ui/ManagerCard';
import ApiService from '@services/api.service';

interface LogRotationStatus {
  enabled: boolean;
  scheduleHours: number;
  lastRotationTime: string | null;
  nextScheduledRotation: string | null;
  lastRotationSuccess: boolean;
  lastRotationError: string | null;
}

interface LogRotationManagerProps {
  isAdmin: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const LogRotationManager: React.FC<LogRotationManagerProps> = ({ isAdmin }) => {
  const { t } = useTranslation();
  const { isDockerAvailable } = useDockerSocket();
  const { addNotification } = useNotifications();

  const [status, setStatus] = useState<LogRotationStatus | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Track local starting state for immediate UI feedback
  const [isStartingRotation, setIsStartingRotation] = useState(false);

  const fetchStatus = useCallback(async () => {
    try {
      const data = (await ApiService.getLogRotationStatus()) as LogRotationStatus;
      setStatus(data);
    } catch (err) {
      console.error('Failed to fetch log rotation status:', err);
    } finally {
      setIsLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchStatus();
  }, [fetchStatus]);

  const handleForceRotation = async () => {
    if (!isAdmin) return;

    setIsStartingRotation(true);
    try {
      const data = (await ApiService.triggerLogRotation()) as {
        success: boolean;
        message?: string;
      };

      if (data.success) {
        // Show success notification
        addNotification({
          type: 'generic',
          status: 'completed',
          message: t('management.logRotation.rotationSuccess'),
          details: { notificationType: 'success' }
        });
      } else {
        // Show error notification
        addNotification({
          type: 'generic',
          status: 'failed',
          message: t('management.logRotation.rotationFailed'),
          error: data.message || t('management.logRotation.rotationFailed'),
          details: { notificationType: 'error' }
        });
      }

      // Refresh status
      await fetchStatus();
    } catch (err: unknown) {
      // Show error notification
      addNotification({
        type: 'generic',
        status: 'failed',
        message: t('management.logRotation.triggerFailed'),
        error: err instanceof Error ? err.message : String(err),
        details: { notificationType: 'error' }
      });
    } finally {
      setIsStartingRotation(false);
    }
  };

  if (isLoading) {
    return <LoadingState message={t('management.logRotation.loadingStatus')} />;
  }

  if (!status) {
    return (
      <Alert color="yellow">
        <span className="text-sm">{t('management.logRotation.unableToLoad')}</span>
      </Alert>
    );
  }

  if (!status.enabled) {
    return (
      <Alert color="yellow">
        <div className="min-w-0">
          <p className="font-medium">{t('management.logRotation.disabled')}</p>
          <p className="text-sm mt-1 mb-2">{t('management.logRotation.addEnvVar')}</p>
          <pre className="px-3 py-2 rounded text-xs overflow-x-auto break-all whitespace-pre-wrap bg-themed-tertiary">
            - NginxLogRotation__Enabled=true
          </pre>
        </div>
      </Alert>
    );
  }

  return (
    <div className="space-y-4">
      {/* Schedule redirect note */}
      <p className="text-xs text-themed-muted">{t('management.schedules.configuredInSchedules')}</p>

      {/* Docker Socket Warning */}
      {!isDockerAvailable && (
        <Alert color="orange">
          <div className="min-w-0">
            <p className="font-medium">{t('management.logRotation.dockerSocketUnavailable')}</p>
            <p className="text-sm mt-1">{t('management.logRotation.dockerSocketDescription')}</p>
            <p className="text-sm mt-2">{t('management.logRotation.addDockerVolume')}</p>
            <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1 break-all">
              /var/run/docker.sock:/var/run/docker.sock
            </code>
          </div>
        </Alert>
      )}

      {/* Status Row */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {/* Last Rotation */}
        <div className="p-4 rounded-lg bg-themed-tertiary">
          <div className="flex items-center gap-2 mb-2">
            {status.lastRotationSuccess ? (
              <CheckCircle2 className="w-4 h-4 icon-success" />
            ) : status.lastRotationTime ? (
              <XCircle className="w-4 h-4 icon-error" />
            ) : (
              <Clock className="w-4 h-4 text-themed-muted" />
            )}
            <span className="text-sm font-medium text-themed-primary">
              {t('management.logRotation.lastRotation')}
            </span>
          </div>
          {status.lastRotationTime ? (
            <>
              <p className="text-lg font-semibold text-themed-primary">
                {formatDateTime(status.lastRotationTime)}
              </p>
              {!status.lastRotationSuccess && status.lastRotationError && (
                <p className="text-xs mt-1 text-themed-error">{status.lastRotationError}</p>
              )}
            </>
          ) : (
            <p className="text-lg font-semibold text-themed-muted">
              {t('management.logRotation.never')}
            </p>
          )}
        </div>

        {/* Next Rotation */}
        <div className="p-4 rounded-lg bg-themed-tertiary">
          <div className="flex items-center gap-2 mb-2">
            <Clock className="w-4 h-4 text-themed-muted" />
            <span className="text-sm font-medium text-themed-primary">
              {t('management.logRotation.nextRotation')}
            </span>
          </div>
          {status.nextScheduledRotation && status.scheduleHours > 0 ? (
            <p className="text-lg font-semibold text-themed-primary">
              {formatDateTime(status.nextScheduledRotation)}
            </p>
          ) : (
            <p className="text-lg font-semibold text-themed-muted">
              {status.scheduleHours === 0
                ? t('management.logRotation.disabled')
                : t('management.logRotation.notScheduled')}
            </p>
          )}
        </div>
      </div>

      {/* Force Rotation Button */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-2">
        <div>
          <p className="text-sm text-themed-primary font-medium">
            {t('management.logRotation.manualRotation')}
          </p>
          <p className="text-xs text-themed-muted">
            {t('management.logRotation.manualRotationDesc')}
          </p>
        </div>
        <Button
          onClick={handleForceRotation}
          disabled={!isAdmin || isStartingRotation}
          variant="outline"
          loading={isStartingRotation}
          leftSection={!isStartingRotation ? <RefreshCw className="w-4 h-4" /> : undefined}
          className="w-full sm:w-auto"
        >
          {isStartingRotation
            ? t('management.logRotation.rotating')
            : t('management.logRotation.rotateNow')}
        </Button>
      </div>
    </div>
  );
};

export default LogRotationManager;
