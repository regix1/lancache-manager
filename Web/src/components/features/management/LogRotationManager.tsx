import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { useDockerSocket } from '@contexts/DockerSocketContext';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { formatDateTime } from '@utils/formatters';
import { RefreshCw, Clock, CheckCircle2, XCircle, Loader2 } from 'lucide-react';
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
  isAuthenticated: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
}

const LogRotationManager: React.FC<LogRotationManagerProps> = ({
  isAuthenticated,
  onError,
  onSuccess
}) => {
  const { t } = useTranslation();
  const { isDockerAvailable } = useDockerSocket();

  const SCHEDULE_OPTIONS: DropdownOption[] = [
    { value: '0', label: t('management.logRotation.schedule.disabled'), description: t('management.logRotation.schedule.disabledDesc') },
    { value: '1', label: t('management.logRotation.schedule.everyHour'), description: t('management.logRotation.schedule.everyHourDesc') },
    { value: '6', label: t('management.logRotation.schedule.every6Hours'), description: t('management.logRotation.schedule.every6HoursDesc') },
    { value: '12', label: t('management.logRotation.schedule.every12Hours'), description: t('management.logRotation.schedule.every12HoursDesc') },
    { value: '24', label: t('management.logRotation.schedule.daily'), description: t('management.logRotation.schedule.dailyDesc') },
    { value: '48', label: t('management.logRotation.schedule.every2Days'), description: t('management.logRotation.schedule.every2DaysDesc') },
    { value: '168', label: t('management.logRotation.schedule.weekly'), description: t('management.logRotation.schedule.weeklyDesc') }
  ];

  const [status, setStatus] = useState<LogRotationStatus | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isRotating, setIsRotating] = useState(false);
  const [isUpdatingSchedule, setIsUpdatingSchedule] = useState(false);

  const fetchStatus = useCallback(async () => {
    try {
      const response = await fetch('/api/system/log-rotation/status', ApiService.getFetchOptions());
      if (response.ok) {
        const data = await response.json();
        setStatus(data);
      }
    } catch (err) {
      console.error('Failed to fetch log rotation status:', err);
    } finally {
      setIsLoading(false);
    }
  }, []);

  const handleScheduleChange = async (value: string) => {
    if (!isAuthenticated || isUpdatingSchedule) return;

    const hours = parseInt(value, 10);
    setIsUpdatingSchedule(true);

    try {
      const response = await fetch('/api/system/log-rotation/schedule', ApiService.getFetchOptions({
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scheduleHours: hours })
      }));

      const data = await response.json();

      if (data.success) {
        setStatus(data.status);
        onSuccess?.(
          `Schedule updated to ${SCHEDULE_OPTIONS.find((o) => o.value === value)?.label || value}`
        );
      } else {
        onError?.(data.message || t('management.logRotation.failedToUpdateSchedule'));
      }
    } catch {
      onError?.('Failed to update schedule');
    } finally {
      setIsUpdatingSchedule(false);
    }
  };

  useEffect(() => {
    fetchStatus();
  }, [fetchStatus]);

  const handleForceRotation = async () => {
    if (!isAuthenticated) return;

    setIsRotating(true);
    try {
      const response = await fetch('/api/system/log-rotation/trigger', ApiService.getFetchOptions({
        method: 'POST'
      }));

      const data = await response.json();

      if (data.success) {
        onSuccess?.(t('management.logRotation.rotationSuccess'));
      } else {
        onError?.(data.message || t('management.logRotation.rotationFailed'));
      }

      // Refresh status
      await fetchStatus();
    } catch {
      onError?.(t('management.logRotation.triggerFailed'));
    } finally {
      setIsRotating(false);
    }
  };

  const getScheduleValue = (hours: number): string => {
    // Find matching option or fall back to closest
    const option = SCHEDULE_OPTIONS.find((o) => o.value === String(hours));
    return option ? option.value : '24'; // Default to daily if not found
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
          <p className="text-sm mt-1 mb-2">
            {t('management.logRotation.addEnvVar')}
          </p>
          <pre className="px-3 py-2 rounded text-xs overflow-x-auto break-all whitespace-pre-wrap bg-themed-tertiary">
            - NginxLogRotation__Enabled=true
          </pre>
        </div>
      </Alert>
    );
  }

  return (
    <div className="space-y-4">
      {/* Schedule Selection */}
      <div className="p-4 rounded-lg bg-themed-tertiary">
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <div>
            <p className="text-sm font-medium text-themed-primary">{t('management.logRotation.rotationSchedule')}</p>
            <p className="text-xs text-themed-muted mt-1">
              {status.scheduleHours > 0
                ? t('management.logRotation.runsAutomatically')
                : t('management.logRotation.enableScheduled')}
            </p>
          </div>
          <div className="relative min-w-[180px]">
            {isUpdatingSchedule && (
              <div className="absolute inset-0 flex items-center justify-center bg-themed-bg-secondary/50 rounded z-10">
                <Loader2 className="w-4 h-4 animate-spin text-themed-primary" />
              </div>
            )}
            <EnhancedDropdown
              options={SCHEDULE_OPTIONS}
              value={getScheduleValue(status.scheduleHours)}
              onChange={handleScheduleChange}
              disabled={!isAuthenticated || isUpdatingSchedule}
              dropdownWidth="280px"
              alignRight
            />
          </div>
        </div>
      </div>

      {/* Docker Socket Warning */}
      {!isDockerAvailable && (
        <Alert color="orange">
          <div className="min-w-0">
            <p className="font-medium">{t('management.logRotation.dockerSocketUnavailable')}</p>
            <p className="text-sm mt-1">
              {t('management.logRotation.dockerSocketDescription')}
            </p>
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
            <span className="text-sm font-medium text-themed-primary">{t('management.logRotation.lastRotation')}</span>
          </div>
          {status.lastRotationTime ? (
            <>
              <p className="text-lg font-semibold text-themed-primary">
                {formatDateTime(status.lastRotationTime)}
              </p>
              {!status.lastRotationSuccess && status.lastRotationError && (
                <p className="text-xs mt-1 text-themed-error">
                  {status.lastRotationError}
                </p>
              )}
            </>
          ) : (
            <p className="text-lg font-semibold text-themed-muted">{t('management.logRotation.never')}</p>
          )}
        </div>

        {/* Next Rotation */}
        <div className="p-4 rounded-lg bg-themed-tertiary">
          <div className="flex items-center gap-2 mb-2">
            <Clock className="w-4 h-4 text-themed-muted" />
            <span className="text-sm font-medium text-themed-primary">{t('management.logRotation.nextRotation')}</span>
          </div>
          {status.nextScheduledRotation && status.scheduleHours > 0 ? (
            <p className="text-lg font-semibold text-themed-primary">
              {formatDateTime(status.nextScheduledRotation)}
            </p>
          ) : (
            <p className="text-lg font-semibold text-themed-muted">
              {status.scheduleHours === 0 ? t('management.logRotation.disabled') : t('management.logRotation.notScheduled')}
            </p>
          )}
        </div>
      </div>

      {/* Force Rotation Button */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 pt-2">
        <div>
          <p className="text-sm text-themed-primary font-medium">{t('management.logRotation.manualRotation')}</p>
          <p className="text-xs text-themed-muted">{t('management.logRotation.manualRotationDesc')}</p>
        </div>
        <Button
          onClick={handleForceRotation}
          disabled={!isAuthenticated || isRotating}
          variant="outline"
          loading={isRotating}
          leftSection={!isRotating ? <RefreshCw className="w-4 h-4" /> : undefined}
          className="w-full sm:w-auto"
        >
          {isRotating ? t('management.logRotation.rotating') : t('management.logRotation.rotateNow')}
        </Button>
      </div>
    </div>
  );
};

export default LogRotationManager;
