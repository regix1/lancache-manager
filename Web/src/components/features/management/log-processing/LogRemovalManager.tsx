import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { FileText, AlertTriangle, Loader2, Trash2 } from 'lucide-react';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import { useNotifications } from '@contexts/notifications';
import { useDockerSocket } from '@contexts/DockerSocketContext';
import { Card } from '@components/ui/Card';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Modal } from '@components/ui/Modal';
import { Tooltip } from '@components/ui/Tooltip';
import { DatasourceListItem } from '@components/ui/DatasourceListItem';
import {
  ManagerCardHeader,
  LoadingState,
  EmptyState,
  ReadOnlyBadge
} from '@components/ui/ManagerCard';
import type { DatasourceServiceCounts } from '@/types';

// Main services that should always be shown first
const MAIN_SERVICES = [
  'steam',
  'epic',
  'riot',
  'blizzard',
  'origin',
  'uplay',
  'gog',
  'wsus',
  'microsoft',
  'sony',
  'nintendo',
  'apple'
];

const ServiceButton: React.FC<{
  service: string;
  count: number;
  isRemoving: boolean;
  isDisabled: boolean;
  onClick: () => void;
  clearLabel: string;
  entriesLabel: string;
  removingLabel: string;
}> = ({ service, count, isRemoving, isDisabled, onClick, clearLabel, entriesLabel, removingLabel }) => {
  return (
    <Button
      onClick={onClick}
      disabled={isDisabled}
      variant="outline"
      loading={isRemoving}
      className="flex flex-col items-center min-h-[60px] justify-center"
      fullWidth
    >
      {!isRemoving ? (
        <>
          <span className="capitalize font-medium text-sm sm:text-base">{clearLabel} {service}</span>
          <span className="text-xs text-themed-muted mt-1">({count.toLocaleString()} {entriesLabel})</span>
        </>
      ) : (
        <span className="capitalize font-medium text-sm sm:text-base">{removingLabel}</span>
      )}
    </Button>
  );
};

interface LogRemovalManagerProps {
  authMode: AuthMode;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const LogRemovalManager: React.FC<LogRemovalManagerProps> = ({
  authMode,
  mockMode,
  onError
}) => {
  const { t } = useTranslation();
  const { notifications, isAnyRemovalRunning } = useNotifications();
  const { isDockerAvailable } = useDockerSocket();

  // State
  const [datasourceCounts, setDatasourceCounts] = useState<DatasourceServiceCounts[]>([]);
  const [expandedDatasources, setExpandedDatasources] = useState<Set<string>>(new Set());
  const [pendingServiceRemoval, setPendingServiceRemoval] = useState<{ datasource: string; service: string } | null>(null);
  const [pendingLogFileDeletion, setPendingLogFileDeletion] = useState<string | null>(null);
  const [deletingLogFile, setDeletingLogFile] = useState<string | null>(null);
  const [showMoreServices, setShowMoreServices] = useState<Record<string, boolean>>({});
  const [isLoading, setIsLoading] = useState(true);
  const [hasInitiallyLoaded, setHasInitiallyLoaded] = useState(false);
  const [logsReadOnly, setLogsReadOnly] = useState(false);
  const [checkingPermissions, setCheckingPermissions] = useState(true);
  const [startingServiceRemoval, setStartingServiceRemoval] = useState<string | null>(null);

  // Derive active log removal from notifications
  const activeLogRemovalNotification = notifications.find(
    n => n.type === 'log_removal' && n.status === 'running'
  );
  const activeLogRemoval = activeLogRemovalNotification?.details?.service as string | null ?? null;

  useEffect(() => {
    if (!hasInitiallyLoaded) {
      setTimeout(() => {
        loadData();
      }, 100);
      loadDirectoryPermissions();
    }
  }, [hasInitiallyLoaded]);

  // Listen for log removal completion via notifications to trigger reload
  useEffect(() => {
    const completedLogRemoval = notifications.find(
      n => n.type === 'log_removal' && n.status === 'completed'
    );
    
    if (completedLogRemoval && hasInitiallyLoaded) {
      loadData(true);
    }
  }, [notifications, hasInitiallyLoaded]);

  const loadData = async (_forceRefresh = false) => {
    setIsLoading(true);
    try {
      const dsCounts = await ApiService.getServiceLogCountsByDatasource();
      setDatasourceCounts(dsCounts);
      setHasInitiallyLoaded(true);
    } catch (err: unknown) {
      console.error('Failed to load log data:', err);
    } finally {
      setIsLoading(false);
    }
  };

  const loadDirectoryPermissions = async () => {
    try {
      setCheckingPermissions(true);
      const data = await ApiService.getDirectoryPermissions();
      setLogsReadOnly(data.logs.readOnly);
    } catch (err) {
      console.error('Failed to check directory permissions:', err);
      setLogsReadOnly(false);
    } finally {
      setCheckingPermissions(false);
    }
  };

  const executeRemoveServiceLogs = async (datasourceName: string, serviceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }

    setPendingServiceRemoval(null);
    setStartingServiceRemoval(`${datasourceName}:${serviceName}`);

    try {
      const result = await ApiService.removeServiceFromDatasourceLogs(datasourceName, serviceName);
      if (result && result.status === 'started') {
        // SignalR will handle progress
      } else {
        onError?.(t('management.logRemoval.errors.unexpectedResponse', { service: serviceName }));
      }
    } catch (err: unknown) {
      const errMsg = err instanceof Error ? err.message : String(err);
      const errorMessage = errMsg?.includes('read-only')
        ? t('management.logRemoval.errors.readOnly')
        : errMsg || t('management.logRemoval.errors.actionFailed');
      onError?.(errorMessage);
    } finally {
      setStartingServiceRemoval(null);
    }
  };

  const handleRemoveServiceLogs = useCallback(
    (datasourceName: string, serviceName: string) => {
      if (authMode !== 'authenticated') {
        onError?.(t('common.fullAuthRequired'));
        return;
      }
      setPendingServiceRemoval({ datasource: datasourceName, service: serviceName });
    },
    [authMode, onError, t]
  );

  const executeDeleteLogFile = async (datasourceName: string) => {
    if (authMode !== 'authenticated') {
      onError?.(t('common.fullAuthRequired'));
      return;
    }

    setPendingLogFileDeletion(null);
    setDeletingLogFile(datasourceName);

    try {
      await ApiService.deleteLogFile(datasourceName);
      // Refresh data after deletion
      await loadData(true);
    } catch (err: unknown) {
      const errMsg = err instanceof Error ? err.message : String(err);
      const errorMessage = errMsg?.includes('read-only')
        ? t('management.logRemoval.errors.readOnly')
        : errMsg || t('management.logRemoval.errors.deleteFailed');
      onError?.(errorMessage);
    } finally {
      setDeletingLogFile(null);
    }
  };

  const getServicesForDatasource = useCallback((ds: DatasourceServiceCounts) => {
    const allServices = Object.keys(ds.serviceCounts).filter((s) => ds.serviceCounts[s] > 0);
    const main = allServices.filter((s) => MAIN_SERVICES.includes(s.toLowerCase())).sort();
    const other = allServices.filter((s) => !MAIN_SERVICES.includes(s.toLowerCase())).sort();
    const showMore = showMoreServices[ds.datasource] ?? false;
    const displayed = showMore ? [...main, ...other] : main;
    return { main, other, displayed };
  }, [showMoreServices]);

  const toggleDatasourceExpanded = (name: string) => {
    setExpandedDatasources(prev => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  };

  const hasAnyLogEntries = datasourceCounts.some(ds =>
    Object.values(ds.serviceCounts).some(count => count > 0)
  );

  const isReadOnly = logsReadOnly || !isDockerAvailable;

  // Help content
  const helpContent = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('management.logRemoval.help.whatThisDoes.title')}>
        {t('management.logRemoval.help.whatThisDoes.description')}
      </HelpSection>

      <HelpSection title={t('management.logRemoval.help.whatItAffects.title')} variant="subtle">
        <ul className="list-disc list-inside text-sm space-y-1">
          <li>{t('management.logRemoval.help.whatItAffects.logFiles')}</li>
          <li>{t('management.logRemoval.help.whatItAffects.databaseRecords')}</li>
        </ul>
      </HelpSection>

      <HelpNote type="info">
        {t('management.logRemoval.help.note')}
      </HelpNote>
    </HelpPopover>
  );

  // Header actions
  const headerActions = (
    <Tooltip content={t('management.logRemoval.refreshServiceCounts')} position="top">
      <Button
        onClick={() => loadData(true)}
        disabled={isLoading || isAnyRemovalRunning}
        variant="subtle"
        size="sm"
      >
        {isLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : t('common.refresh')}
      </Button>
    </Tooltip>
  );

  return (
    <>
      <Card>
        <ManagerCardHeader
          icon={FileText}
          iconColor="orange"
          title={t('management.logRemoval.title')}
          subtitle={t('management.logRemoval.subtitle')}
          helpContent={helpContent}
          permissions={{
            logsReadOnly,
            checkingPermissions
          }}
          actions={headerActions}
        />

        {/* Read-Only Warning */}
        {logsReadOnly && (
          <Alert color="orange" className="mb-6">
            <div>
              <p className="font-medium">{t('management.logRemoval.alerts.logsReadOnly.title')}</p>
              <p className="text-sm mt-1">
                {t('management.logRemoval.alerts.logsReadOnly.description')}
              </p>
            </div>
          </Alert>
        )}

        {/* Docker Socket Warning */}
        {!isDockerAvailable && !logsReadOnly && (
          <Alert color="orange" className="mb-6">
            <div className="min-w-0">
              <p className="font-medium">{t('management.logRemoval.alerts.dockerSocket.title')}</p>
              <p className="text-sm mt-1">
                {t('management.logRemoval.alerts.dockerSocket.description')}
              </p>
              <p className="text-sm mt-2">
                {t('management.logRemoval.alerts.dockerSocket.addVolumes')}
              </p>
              <code className="block bg-themed-tertiary px-2 py-1 rounded text-xs mt-1 break-all">
                - /var/run/docker.sock:/var/run/docker.sock
              </code>
            </div>
          </Alert>
        )}

        {/* Content */}
        {isReadOnly ? (
          <ReadOnlyBadge message={logsReadOnly ? t('management.logRemoval.readOnly') : t('management.logRemoval.dockerSocketRequired')} />
        ) : (
          <>
            {isLoading ? (
              <LoadingState
                message={t('management.logRemoval.loading.scanning')}
                submessage={t('management.logRemoval.loading.mayTakeMinutes')}
              />
            ) : hasAnyLogEntries ? (
              <div className="space-y-3">
                {datasourceCounts.map((ds) => {
                  const { other, displayed } = getServicesForDatasource(ds);
                  const isExpanded = expandedDatasources.has(ds.datasource);
                  const totalEntries = Object.values(ds.serviceCounts).reduce((a, b) => a + b, 0);
                  const hasEntries = totalEntries > 0;

                  return (
                    <DatasourceListItem
                      key={ds.datasource}
                      name={ds.datasource}
                      path={ds.logsPath}
                      isExpanded={isExpanded}
                      onToggle={() => toggleDatasourceExpanded(ds.datasource)}
                      enabled={ds.enabled && ds.logsWritable}
                      statusBadge={`${totalEntries.toLocaleString()} entries`}
                    >
                      {hasEntries ? (
                        <>
                          {/* Delete entire log file button */}
                          <div className="flex justify-end pt-2 pb-1">
                            <Button
                              variant="outline"
                              size="sm"
                              color="red"
                              leftSection={<Trash2 className="w-3 h-3" />}
                              onClick={(e) => {
                                e.stopPropagation();
                                setPendingLogFileDeletion(ds.datasource);
                              }}
                              disabled={
                                mockMode ||
                                isAnyRemovalRunning ||
                                !!startingServiceRemoval ||
                                !!deletingLogFile ||
                                authMode !== 'authenticated' ||
                                !ds.logsWritable ||
                                !isDockerAvailable ||
                                checkingPermissions
                              }
                              loading={deletingLogFile === ds.datasource}
                              className="w-full sm:w-auto"
                            >
                              {t('management.logRemoval.buttons.deleteLogFile')}
                            </Button>
                          </div>

                          <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
                            {displayed.map((service) => {
                              const key = `${ds.datasource}:${service}`;
                              return (
                                <ServiceButton
                                  key={key}
                                  service={service}
                                  count={ds.serviceCounts[service] || 0}
                                  isRemoving={activeLogRemoval === service || startingServiceRemoval === key}
                                  isDisabled={
                                    mockMode ||
                                    isAnyRemovalRunning ||
                                    !!startingServiceRemoval ||
                                    authMode !== 'authenticated' ||
                                    !ds.logsWritable ||
                                    !isDockerAvailable ||
                                    checkingPermissions
                                  }
                                  onClick={() => handleRemoveServiceLogs(ds.datasource, service)}
                                  clearLabel={t('management.logRemoval.buttons.clear')}
                                  entriesLabel={t('management.logRemoval.labels.entries')}
                                  removingLabel={t('management.logRemoval.labels.removing')}
                                />
                              );
                            })}
                          </div>

                          {other.length > 0 && (
                            <div className="mt-3 text-center">
                              <Button
                                variant="default"
                                size="sm"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  setShowMoreServices(prev => ({
                                    ...prev,
                                    [ds.datasource]: !prev[ds.datasource]
                                  }));
                                }}
                              >
                                {showMoreServices[ds.datasource] ? (
                                  <>{t('management.logRemoval.buttons.showLess', { count: other.length })}</>
                                ) : (
                                  <>{t('management.logRemoval.buttons.showMore', { count: other.length })}</>
                                )}
                              </Button>
                            </div>
                          )}
                        </>
                      ) : (
                        <div className="text-center py-4 text-themed-muted text-sm">
                          {t('management.logRemoval.noEntriesForDatasource')}
                        </div>
                      )}
                    </DatasourceListItem>
                  );
                })}
              </div>
            ) : (
              <EmptyState
                title={t('management.logRemoval.emptyState.title')}
                subtitle={t('management.logRemoval.emptyState.subtitle')}
              />
            )}
          </>
        )}
      </Card>

      {/* Log Removal Confirmation Modal */}
      <Modal
        opened={pendingServiceRemoval !== null}
        onClose={() => {
          if (!startingServiceRemoval) {
            setPendingServiceRemoval(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>{t('management.logRemoval.modal.removeServiceLogs')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.logRemoval.modal.removeQuestion', {
              service: pendingServiceRemoval?.service,
              datasource: pendingServiceRemoval?.datasource
            })}
          </p>

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.logRemoval.modal.important')}:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.logRemoval.modal.cannotUndo')}</li>
                <li>{t('management.logRemoval.modal.mayTakeMinutes')}</li>
                <li>{t('management.logRemoval.modal.cachedFilesRemain', { service: pendingServiceRemoval?.service })}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingServiceRemoval(null)}
              disabled={!!startingServiceRemoval}
            >
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() =>
                pendingServiceRemoval && executeRemoveServiceLogs(pendingServiceRemoval.datasource, pendingServiceRemoval.service)
              }
              loading={!!startingServiceRemoval}
            >
              {t('management.logRemoval.buttons.removeLogs')}
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Log File Confirmation Modal */}
      <Modal
        opened={pendingLogFileDeletion !== null}
        onClose={() => {
          if (!deletingLogFile) {
            setPendingLogFileDeletion(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Trash2 className="w-6 h-6 text-themed-error" />
            <span>{t('management.logRemoval.modal.deleteEntireLogFile')}</span>
          </div>
        }
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            {t('management.logRemoval.modal.deleteQuestion', { datasource: pendingLogFileDeletion })}
          </p>

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">{t('management.logRemoval.modal.warningDestructive')}:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>{t('management.logRemoval.modal.permanentlyDelete')}</li>
                <li>{t('management.logRemoval.modal.historyLost')}</li>
                <li>{t('management.logRemoval.modal.cannotUndo')}</li>
                <li>{t('management.logRemoval.modal.cachedGamesRemain')}</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingLogFileDeletion(null)}
              disabled={!!deletingLogFile}
            >
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="red"
              onClick={() => pendingLogFileDeletion && executeDeleteLogFile(pendingLogFileDeletion)}
              loading={!!deletingLogFile}
            >
              {t('management.logRemoval.buttons.deleteLogFile')}
            </Button>
          </div>
        </div>
      </Modal>
    </>
  );
};

export default LogRemovalManager;
