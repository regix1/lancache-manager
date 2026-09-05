import React from 'react';
import {
  Shield,
  CheckCircle,
  AlertTriangle,
  XCircle,
  FolderOpen,
  FileText,
  Container
} from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Alert } from '@components/ui/Alert';
import { Button } from '@components/ui/Button';
import { StepHeader } from '@components/initialization/StepHeader';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { Tooltip } from '@components/ui/Tooltip';
import { useDirectoryPermissions } from '@/hooks/useDirectoryPermissions';

interface PermissionsCheckStepProps {
  onComplete: () => void;
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

export const PermissionsCheckStep: React.FC<PermissionsCheckStepProps> = ({ onComplete }) => {
  const { t } = useTranslation();
  const {
    cacheExist,
    cacheWritable,
    cacheReadOnly,
    cachePath,
    logsExist,
    logsWritable,
    logsReadOnly,
    logsPath,
    dockerSocketAvailable,
    checkingPermissions,
    timedOut,
    error,
    reload
  } = useDirectoryPermissions();
  const [showForceContinue, setShowForceContinue] = React.useState(false);

  React.useEffect(() => {
    if (!checkingPermissions) {
      setShowForceContinue(false);
      return;
    }

    const timer = setTimeout(() => {
      setShowForceContinue(true);
    }, 15000);

    return () => clearTimeout(timer);
  }, [checkingPermissions]);

  const getDirectoryStatus = (
    dir: { exists: boolean; writable: boolean; readOnly: boolean },
    impactKey: string
  ): Pick<PermissionCheck, 'status' | 'message' | 'impact'> => {
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
      message: t('initialization.permissionsCheck.checkingFailed'),
      impact: t(`initialization.permissionsCheck.${impactKey}Impact`)
    };
  };

  const buildChecks = (): PermissionCheck[] => {
    if (checkingPermissions) {
      return [
        {
          id: 'cache',
          label: t('initialization.permissionsCheck.cacheDirectory'),
          status: 'loading',
          message: t('initialization.permissionsCheck.checking')
        },
        {
          id: 'logs',
          label: t('initialization.permissionsCheck.logsDirectory'),
          status: 'loading',
          message: t('initialization.permissionsCheck.checking')
        },
        {
          id: 'docker',
          label: t('initialization.permissionsCheck.dockerSocket'),
          status: 'loading',
          message: t('initialization.permissionsCheck.checking')
        }
      ];
    }

    const cacheStatus = getDirectoryStatus(
      { exists: cacheExist, writable: cacheWritable, readOnly: cacheReadOnly },
      'cache'
    );
    const logsStatus = getDirectoryStatus(
      { exists: logsExist, writable: logsWritable, readOnly: logsReadOnly },
      'logs'
    );

    return [
      {
        id: 'cache',
        label: t('initialization.permissionsCheck.cacheDirectory'),
        path: cachePath || undefined,
        ...cacheStatus
      },
      {
        id: 'logs',
        label: t('initialization.permissionsCheck.logsDirectory'),
        path: logsPath || undefined,
        ...logsStatus
      },
      {
        id: 'docker',
        label: t('initialization.permissionsCheck.dockerSocket'),
        status: dockerSocketAvailable ? 'success' : 'warning',
        message: dockerSocketAvailable
          ? t('initialization.permissionsCheck.available')
          : t('initialization.permissionsCheck.notAvailable'),
        impact: dockerSocketAvailable
          ? undefined
          : t('initialization.permissionsCheck.dockerImpact')
      }
    ];
  };

  const checks = buildChecks();

  const getStatusIcon = (status: CheckStatus) => {
    switch (status) {
      case 'loading':
        return <LoadingSpinner inline size="md" className="text-themed-muted" />;
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

  const hasErrors = checks.some((c) => c.status === 'error');
  const allSuccess = checks.every((c) => c.status === 'success');
  const isChecking = checkingPermissions;

  return (
    <div className="space-y-4">
      <StepHeader
        icon={<Shield className="w-7 h-7 icon-info" />}
        iconBackground="bg-themed-info"
        title={t('initialization.permissionsCheck.title')}
        description={t('initialization.permissionsCheck.subtitle')}
      />

      {/* Permission Checks Grid */}
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        {checks.map((check) => (
          <div
            key={check.id}
            className="p-4 rounded-lg border-2 flex flex-col bg-themed-tertiary border-themed-primary"
          >
            {/* Icon and Status Row */}
            <div className="flex items-center justify-between mb-3">
              <div
                className={`w-10 h-10 rounded-lg flex items-center justify-center ${
                  check.status === 'success'
                    ? 'bg-themed-success icon-success'
                    : check.status === 'warning'
                      ? 'bg-themed-warning icon-warning'
                      : check.status === 'error'
                        ? 'bg-themed-error icon-error'
                        : 'bg-themed-secondary text-themed-muted'
                }`}
              >
                {getCheckIcon(check.id)}
              </div>
              {getStatusIcon(check.status)}
            </div>

            {/* Label */}
            <h4 className="font-semibold text-themed-primary text-sm mb-1">{check.label}</h4>

            {/* Status Message */}
            <p
              className={`text-xs font-medium mb-2 ${
                check.status === 'success'
                  ? 'text-themed-success'
                  : check.status === 'warning'
                    ? 'text-themed-warning'
                    : check.status === 'error'
                      ? 'text-themed-error'
                      : 'text-themed-muted'
              }`}
            >
              {check.message}
            </p>

            {/* Path (if exists) */}
            {check.path && (
              <Tooltip content={check.path} position="top" className="block min-w-0 mt-auto">
                <code className="block text-xs px-2 py-1 rounded bg-themed-secondary text-themed-muted truncate">
                  {check.path}
                </code>
              </Tooltip>
            )}

            {/* Impact message (if exists) */}
            {check.impact && (
              <p className="text-xs text-themed-muted mt-2 leading-relaxed">{check.impact}</p>
            )}
          </div>
        ))}
      </div>

      {/* Error message */}
      {error && <Alert color="error">{error}</Alert>}

      {/* Summary Banner */}
      {!isChecking && !error && (
        <div
          className={`p-3 rounded-lg flex items-center gap-3 ${
            allSuccess ? 'bg-themed-success' : hasErrors ? 'bg-themed-error' : 'bg-themed-warning'
          }`}
        >
          {allSuccess ? (
            <CheckCircle className="w-5 h-5 icon-success flex-shrink-0" />
          ) : hasErrors ? (
            <XCircle className="w-5 h-5 icon-error flex-shrink-0" />
          ) : (
            <AlertTriangle className="w-5 h-5 icon-warning flex-shrink-0" />
          )}
          <p
            className={`text-sm ${
              allSuccess
                ? 'text-themed-success'
                : hasErrors
                  ? 'text-themed-error'
                  : 'text-themed-warning'
            }`}
          >
            {allSuccess
              ? t('initialization.permissionsCheck.allGood')
              : hasErrors
                ? t('initialization.permissionsCheck.hasErrors')
                : t('initialization.permissionsCheck.hasWarnings')}
          </p>
        </div>
      )}

      {/* Action Buttons */}
      <div className="setup-actions">
        {!isChecking && (
          <Button variant="filled" color="secondary" onClick={reload} className="sm:w-auto">
            {t('initialization.permissionsCheck.recheck')}
          </Button>
        )}

        <Button
          variant="filled"
          color="secondary"
          onClick={onComplete}
          disabled={isChecking && !showForceContinue}
          className="flex-1"
        >
          {isChecking && showForceContinue
            ? t('initialization.permissionsCheck.continueAnyway')
            : t('initialization.permissionsCheck.continue')}
        </Button>
      </div>

      {timedOut && (
        <Alert color="warning">{t('initialization.permissionsCheck.timeoutMessage')}</Alert>
      )}
    </div>
  );
};
