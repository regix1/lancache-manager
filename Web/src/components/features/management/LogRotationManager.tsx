import React, { useState, useEffect, useCallback } from 'react';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { formatDateTime } from '@utils/formatters';
import {
  RefreshCw,
  Clock,
  CheckCircle2,
  XCircle,
  Loader2,
  Calendar
} from 'lucide-react';

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
    } catch (err) {
      onError?.('Failed to trigger log rotation');
    } finally {
      setIsRotating(false);
    }
  };

  const getScheduleLabel = (hours: number): string => {
    if (hours <= 0) return 'Disabled';
    if (hours === 1) return 'Every hour';
    if (hours === 6) return 'Every 6 hours';
    if (hours === 12) return 'Every 12 hours';
    if (hours === 24) return 'Daily';
    return `Every ${hours} hours`;
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
            Enable <code className="px-1.5 py-0.5 rounded text-xs" style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}>NginxLogRotation:Enabled</code> in your configuration and ensure the Docker socket is mounted.
          </p>
        </div>
      </Alert>
    );
  }

  return (
    <div className="space-y-4">
      {/* Status Grid */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {/* Schedule */}
        <div
          className="p-4 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
          <div className="flex items-center gap-2 mb-2">
            <Calendar className="w-4 h-4 text-themed-muted" />
            <span className="text-sm font-medium text-themed-primary">Schedule</span>
          </div>
          <p className="text-lg font-semibold text-themed-primary">
            {getScheduleLabel(status.scheduleHours)}
          </p>
          {status.scheduleHours > 0 && (
            <p className="text-xs text-themed-muted mt-1">
              Runs automatically at startup and on schedule
            </p>
          )}
        </div>

        {/* Last Rotation */}
        <div
          className="p-4 rounded-lg"
          style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
        >
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
      </div>

      {/* Next Scheduled */}
      {status.nextScheduledRotation && status.scheduleHours > 0 && (
        <div
          className="p-3 rounded-lg flex items-center justify-between"
          style={{
            backgroundColor: 'color-mix(in srgb, var(--theme-primary) 10%, transparent)',
            border: '1px solid color-mix(in srgb, var(--theme-primary) 20%, transparent)'
          }}
        >
          <div className="flex items-center gap-2">
            <Clock className="w-4 h-4" style={{ color: 'var(--theme-primary)' }} />
            <span className="text-sm text-themed-primary">Next scheduled rotation:</span>
          </div>
          <span className="text-sm font-medium" style={{ color: 'var(--theme-primary)' }}>
            {formatDateTime(status.nextScheduledRotation)}
          </span>
        </div>
      )}

      {/* Force Rotation Button */}
      <div className="flex items-center justify-between pt-2">
        <div>
          <p className="text-sm text-themed-primary font-medium">Manual Rotation</p>
          <p className="text-xs text-themed-muted">
            Signal nginx to reopen log files immediately
          </p>
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
