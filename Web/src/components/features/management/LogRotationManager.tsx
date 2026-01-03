import React, { useState, useEffect, useCallback } from 'react';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { formatDateTime } from '@utils/formatters';
import { RefreshCw, Clock, CheckCircle2, XCircle, Loader2 } from 'lucide-react';

const SCHEDULE_OPTIONS: DropdownOption[] = [
  { value: '0', label: 'Disabled', description: 'No automatic rotation' },
  { value: '1', label: 'Every hour', description: 'Rotate logs hourly' },
  { value: '6', label: 'Every 6 hours', description: 'Rotate logs 4 times daily' },
  { value: '12', label: 'Every 12 hours', description: 'Rotate logs twice daily' },
  { value: '24', label: 'Daily', description: 'Rotate logs once per day' },
  { value: '48', label: 'Every 2 days', description: 'Rotate logs every 48 hours' },
  { value: '168', label: 'Weekly', description: 'Rotate logs once per week' }
];

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
  const [status, setStatus] = useState<LogRotationStatus | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isRotating, setIsRotating] = useState(false);
  const [isUpdatingSchedule, setIsUpdatingSchedule] = useState(false);

  const fetchStatus = useCallback(async () => {
    try {
      const response = await fetch('/api/system/log-rotation/status');
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
      const response = await fetch('/api/system/log-rotation/schedule', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scheduleHours: hours })
      });

      const data = await response.json();

      if (data.success) {
        setStatus(data.status);
        onSuccess?.(
          `Schedule updated to ${SCHEDULE_OPTIONS.find((o) => o.value === value)?.label || value}`
        );
      } else {
        onError?.(data.message || 'Failed to update schedule');
      }
    } catch {
      onError?.('Failed to update schedule');
    } finally {
      setIsUpdatingSchedule(false);
    }
  };

  useEffect(() => {
    fetchStatus();
    // Refresh status every minute
    const interval = setInterval(fetchStatus, 60000);
    return () => clearInterval(interval);
  }, [fetchStatus]);

  const handleForceRotation = async () => {
    if (!isAuthenticated) return;

    setIsRotating(true);
    try {
      const response = await fetch('/api/system/log-rotation/trigger', {
        method: 'POST'
      });

      const data = await response.json();

      if (data.success) {
        onSuccess?.('Log rotation completed successfully');
      } else {
        onError?.(data.message || 'Log rotation failed');
      }

      // Refresh status
      await fetchStatus();
    } catch {
      onError?.('Failed to trigger log rotation');
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
    return (
      <div className="flex items-center justify-center py-8">
        <Loader2 className="w-6 h-6 animate-spin text-themed-muted" />
      </div>
    );
  }

  if (!status) {
    return (
      <Alert color="yellow">
        <span className="text-sm">Unable to load log rotation status</span>
      </Alert>
    );
  }

  if (!status.enabled) {
    return (
      <Alert color="yellow">
        <div>
          <p className="font-medium">Log rotation is disabled</p>
          <p className="text-sm mt-1">
            Enable{' '}
            <code
              className="px-1.5 py-0.5 rounded text-xs"
              style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
            >
              NginxLogRotation:Enabled
            </code>{' '}
            in your configuration and ensure the Docker socket is mounted.
          </p>
        </div>
      </Alert>
    );
  }

  return (
    <div className="space-y-4">
      {/* Schedule Selection */}
      <div className="p-4 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
        <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <div>
            <p className="text-sm font-medium text-themed-primary">Rotation Schedule</p>
            <p className="text-xs text-themed-muted mt-1">
              {status.scheduleHours > 0
                ? 'Runs automatically at startup and on schedule'
                : 'Enable scheduled rotation to run automatically'}
            </p>
          </div>
          <div className="relative">
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
              dropdownWidth="200px"
              alignRight
            />
          </div>
        </div>
      </div>

      {/* Status Row */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {/* Last Rotation */}
        <div className="p-4 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
          <div className="flex items-center gap-2 mb-2">
            {status.lastRotationSuccess ? (
              <CheckCircle2 className="w-4 h-4" style={{ color: 'var(--theme-success)' }} />
            ) : status.lastRotationTime ? (
              <XCircle className="w-4 h-4" style={{ color: 'var(--theme-error)' }} />
            ) : (
              <Clock className="w-4 h-4 text-themed-muted" />
            )}
            <span className="text-sm font-medium text-themed-primary">Last Rotation</span>
          </div>
          {status.lastRotationTime ? (
            <>
              <p className="text-lg font-semibold text-themed-primary">
                {formatDateTime(status.lastRotationTime)}
              </p>
              {!status.lastRotationSuccess && status.lastRotationError && (
                <p className="text-xs mt-1" style={{ color: 'var(--theme-error)' }}>
                  {status.lastRotationError}
                </p>
              )}
            </>
          ) : (
            <p className="text-lg font-semibold text-themed-muted">Never</p>
          )}
        </div>

        {/* Next Rotation */}
        <div className="p-4 rounded-lg" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>
          <div className="flex items-center gap-2 mb-2">
            <Clock className="w-4 h-4 text-themed-muted" />
            <span className="text-sm font-medium text-themed-primary">Next Rotation</span>
          </div>
          {status.nextScheduledRotation && status.scheduleHours > 0 ? (
            <p className="text-lg font-semibold text-themed-primary">
              {formatDateTime(status.nextScheduledRotation)}
            </p>
          ) : (
            <p className="text-lg font-semibold text-themed-muted">
              {status.scheduleHours === 0 ? 'Disabled' : 'Not scheduled'}
            </p>
          )}
        </div>
      </div>

      {/* Force Rotation Button */}
      <div className="flex items-center justify-between pt-2">
        <div>
          <p className="text-sm text-themed-primary font-medium">Manual Rotation</p>
          <p className="text-xs text-themed-muted">Signal nginx to reopen log files immediately</p>
        </div>
        <Button
          onClick={handleForceRotation}
          disabled={!isAuthenticated || isRotating}
          variant="outline"
          leftSection={
            isRotating ? (
              <Loader2 className="w-4 h-4 animate-spin" />
            ) : (
              <RefreshCw className="w-4 h-4" />
            )
          }
        >
          {isRotating ? 'Rotating...' : 'Rotate Now'}
        </Button>
      </div>
    </div>
  );
};

export default LogRotationManager;
