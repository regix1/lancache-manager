import React, { useState, useEffect } from 'react';
import { Shield, CheckCircle, AlertTriangle, XCircle, Loader2, RefreshCw, FolderOpen, FileText, Container } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';

interface PermissionsCheckStepProps {
  onComplete: () => void;
}

interface PermissionsData {
  cache: { path: string; exists: boolean; writable: boolean; readOnly: boolean };
  logs: { path: string; exists: boolean; writable: boolean; readOnly: boolean };
  dockerSocket: { available: boolean };
}

type CheckStatus = 'loading' | 'success' | 'warning' | 'error';

interface PermissionCheck {
  id: string;
  label: string;
  path?: string;
  status: CheckStatus;
  message: string;
  impact?: string;
}

export const PermissionsCheckStep: React.FC<PermissionsCheckStepProps> = ({
  onComplete
}) => {
  const { t } = useTranslation();
  const [error, setError] = useState<string | null>(null);
  const [checks, setChecks] = useState<PermissionCheck[]>([]);

  const checkPermissions = async () => {
    setError(null);

    setChecks([
      { id: 'cache', label: t('initialization.permissionsCheck.cacheDirectory'), status: 'loading', message: t('initialization.permissionsCheck.checking') },
      { id: 'logs', label: t('initialization.permissionsCheck.logsDirectory'), status: 'loading', message: t('initialization.permissionsCheck.checking') },
      { id: 'docker', label: t('initialization.permissionsCheck.dockerSocket'), status: 'loading', message: t('initialization.permissionsCheck.checking') }
    ]);

    try {
      const data: PermissionsData = await ApiService.getDirectoryPermissions();

      const getDirectoryStatus = (dir: { exists: boolean; writable: boolean; readOnly: boolean }, impactKey: string): Pick<PermissionCheck, 'status' | 'message' | 'impact'> => {
        if (!dir.exists) {
          return {
            status: 'warning',
            message: t('initialization.permissionsCheck.notFound'),
            impact: t(`initialization.permissionsCheck.${impactKey}NotFound`)
          };
        }
        if (dir.writable) {
          return { status: 'success', message: t('initialization.permissionsCheck.writable') };
        }
        if (dir.readOnly) {
          return {
            status: 'warning',
            message: t('initialization.permissionsCheck.readOnly'),
            impact: t(`initialization.permissionsCheck.${impactKey}Impact`)
          };
        }
        return {
          status: 'error',
          message: t('initialization.permissionsCheck.notAccessible'),
          impact: t(`initialization.permissionsCheck.${impactKey}Impact`)
        };
      };

      const cacheStatus = getDirectoryStatus(data.cache, 'cache');
      const logsStatus = getDirectoryStatus(data.logs, 'logs');

      const newChecks: PermissionCheck[] = [
        {
          id: 'cache',
          label: t('initialization.permissionsCheck.cacheDirectory'),
          path: data.cache.path,
          ...cacheStatus
        },
        {
          id: 'logs',
          label: t('initialization.permissionsCheck.logsDirectory'),
          path: data.logs.path,
          ...logsStatus
        },
        {
          id: 'docker',
          label: t('initialization.permissionsCheck.dockerSocket'),
          status: data.dockerSocket.available ? 'success' : 'warning',
          message: data.dockerSocket.available
            ? t('initialization.permissionsCheck.available')
            : t('initialization.permissionsCheck.notAvailable'),
          impact: data.dockerSocket.available ? undefined : t('initialization.permissionsCheck.dockerImpact')
        }
      ];

      setChecks(newChecks);
    } catch (err) {
      console.error('Failed to check permissions:', err);
      setError(t('initialization.permissionsCheck.failedToCheck'));
    }
  };

  useEffect(() => {
    checkPermissions();
  }, []);

  const getStatusIcon = (status: CheckStatus) => {
    switch (status) {
      case 'loading':
        return <Loader2 className="w-5 h-5 animate-spin text-themed-muted" />;
      case 'success':
        return <CheckCircle className="w-5 h-5 icon-success" />;
      case 'warning':
        return <AlertTriangle className="w-5 h-5 icon-warning" />;
      case 'error':
        return <XCircle className="w-5 h-5 icon-error" />;
    }
  };

  const getCheckIcon = (id: string) => {
    switch (id) {
      case 'cache':
        return <FolderOpen className="w-5 h-5" />;
      case 'logs':
        return <FileText className="w-5 h-5" />;
      case 'docker':
        return <Container className="w-5 h-5" />;
      default:
        return null;
    }
  };

  const hasErrors = checks.some(c => c.status === 'error');
  const allSuccess = checks.every(c => c.status === 'success');
  const isChecking = checks.some(c => c.status === 'loading');

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex flex-col items-center text-center">
        <div className="w-14 h-14 rounded-full flex items-center justify-center mb-3 bg-themed-info">
          <Shield className="w-7 h-7 icon-info" />
        </div>
        <h3 className="text-lg font-semibold text-themed-primary mb-1">
          {t('initialization.permissionsCheck.title')}
        </h3>
        <p className="text-sm text-themed-secondary max-w-md">
          {t('initialization.permissionsCheck.subtitle')}
        </p>
      </div>

      {/* Permission Checks Grid */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        {checks.map((check) => (
          <div
            key={check.id}
            className="p-4 rounded-lg border-2 flex flex-col bg-themed-tertiary border-themed-primary"
          >
            {/* Icon and Status Row */}
            <div className="flex items-center justify-between mb-3">
              <div className={`w-10 h-10 rounded-lg flex items-center justify-center ${
                check.status === 'success'
                  ? 'bg-themed-success icon-success'
                  : check.status === 'warning'
                    ? 'bg-themed-warning icon-warning'
                    : check.status === 'error'
                      ? 'bg-themed-error icon-error'
                      : 'bg-themed-secondary text-themed-muted'
              }`}>
                {getCheckIcon(check.id)}
              </div>
              {getStatusIcon(check.status)}
            </div>

            {/* Label */}
            <h4 className="font-semibold text-themed-primary text-sm mb-1">
              {check.label}
            </h4>

            {/* Status Message */}
            <p className={`text-xs font-medium mb-2 ${
              check.status === 'success'
                ? 'text-themed-success'
                : check.status === 'warning'
                  ? 'text-themed-warning'
                  : check.status === 'error'
                    ? 'text-themed-error'
                    : 'text-themed-muted'
            }`}>
              {check.message}
            </p>

            {/* Path (if exists) */}
            {check.path && (
              <code className="text-xs px-2 py-1 rounded bg-themed-secondary text-themed-muted truncate mt-auto" title={check.path}>
                {check.path}
              </code>
            )}

            {/* Impact message (if exists) */}
            {check.impact && (
              <p className="text-xs text-themed-muted mt-2 leading-relaxed">
                {check.impact}
              </p>
            )}
          </div>
        ))}
      </div>

      {/* Error message */}
      {error && (
        <div className="p-3 rounded-lg bg-themed-error">
          <p className="text-sm text-themed-error">{error}</p>
        </div>
      )}

      {/* Summary Banner */}
      {!isChecking && !error && (
        <div className={`p-3 rounded-lg flex items-center gap-3 ${
          allSuccess ? 'bg-themed-success' : hasErrors ? 'bg-themed-error' : 'bg-themed-warning'
        }`}>
          {allSuccess ? (
            <CheckCircle className="w-5 h-5 icon-success flex-shrink-0" />
          ) : hasErrors ? (
            <XCircle className="w-5 h-5 icon-error flex-shrink-0" />
          ) : (
            <AlertTriangle className="w-5 h-5 icon-warning flex-shrink-0" />
          )}
          <p className={`text-sm ${
            allSuccess ? 'text-themed-success' : hasErrors ? 'text-themed-error' : 'text-themed-warning'
          }`}>
            {allSuccess
              ? t('initialization.permissionsCheck.allGood')
              : hasErrors
                ? t('initialization.permissionsCheck.hasErrors')
                : t('initialization.permissionsCheck.hasWarnings')}
          </p>
        </div>
      )}

      {/* Action Buttons */}
      <div className="flex flex-col sm:flex-row gap-2">
        {!isChecking && (
          <Button
            variant="outline"
            onClick={checkPermissions}
            className="sm:w-auto"
            leftSection={<RefreshCw className="w-4 h-4" />}
          >
            {t('initialization.permissionsCheck.recheck')}
          </Button>
        )}

        <Button
          variant="filled"
          color="green"
          onClick={onComplete}
          disabled={isChecking}
          className="flex-1"
        >
          {t('initialization.permissionsCheck.continue')}
        </Button>
      </div>
    </div>
  );
};
