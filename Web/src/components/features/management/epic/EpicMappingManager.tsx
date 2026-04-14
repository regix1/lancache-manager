import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { ExternalLink, CheckCircle, XCircle, Calendar } from 'lucide-react';
import { EpicIcon } from '@components/ui/EpicIcon';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { ManagerCardHeader } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useNotifications } from '@contexts/notifications';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import type { EpicMappingAuthStatus, EpicMappingStats } from '../../../../types';

interface EpicMappingManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onNavigateToEpicLogin?: () => void;
  onNavigateToSchedule?: () => void;
}

const EpicMappingManager: React.FC<EpicMappingManagerProps> = ({
  isAdmin,
  mockMode,
  onError,
  onNavigateToEpicLogin,
  onNavigateToSchedule
}) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();
  const { notifications } = useNotifications();

  // Derive Epic mapping operation state from notifications (standardized pattern)
  const activeEpicNotification = notifications.find(
    (n) => n.type === 'epic_game_mapping' && n.status === 'running'
  );
  const isEpicMappingFromNotification = !!activeEpicNotification;

  const [authStatus, setAuthStatus] = useState<EpicMappingAuthStatus | null>(null);
  const [stats, setStats] = useState<EpicMappingStats | null>(null);

  const isAuthenticated = authStatus?.isAuthenticated ?? false;

  const loadStatus = useCallback(async () => {
    try {
      const [auth, statsData] = await Promise.all([
        ApiService.getEpicMappingAuthStatus(),
        ApiService.getEpicMappingStats()
      ]);
      setAuthStatus(auth);
      setStats(statsData);
    } catch {
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0
      });
      setStats(null);
    }
  }, []);

  useEffect(() => {
    loadStatus();
  }, [loadStatus]);

  // Refresh on SignalR events
  useEffect(() => {
    const handleUpdate = () => {
      loadStatus();
    };
    on('EpicGameMappingsUpdated', handleUpdate);
    on('EpicDaemonSessionCreated', handleUpdate);
    on('EpicDaemonSessionUpdated', handleUpdate);
    on('EpicDaemonSessionTerminated', handleUpdate);
    on('EpicMappingProgress', handleUpdate);
    return () => {
      off('EpicGameMappingsUpdated', handleUpdate);
      off('EpicDaemonSessionCreated', handleUpdate);
      off('EpicDaemonSessionUpdated', handleUpdate);
      off('EpicDaemonSessionTerminated', handleUpdate);
      off('EpicMappingProgress', handleUpdate);
    };
  }, [on, off, loadStatus]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadStatus();
    }
  }, [connectionState, loadStatus]);

  const formattedLastCollection = useFormattedDateTime(authStatus?.lastCollectionUtc ?? null);

  const handleRefresh = async () => {
    if (isEpicMappingFromNotification) return;

    try {
      const result = await ApiService.startEpicRefresh();
      if (!result.started) {
        onError?.(result.message || 'A refresh is already in progress');
      }
      // Progress is tracked via SignalR notification bar — no inline success message needed
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to start Epic catalog refresh');
    }
  };

  return (
    <Card>
      <ManagerCardHeader
        icon={EpicIcon}
        iconColor="purple"
        title={t('management.epicMapping.title')}
        subtitle={t('management.epicMapping.subtitle')}
        helpContent={
          <HelpPopover position="left" width={320}>
            <HelpSection title={t('management.epicMapping.help.howItWorks.title')} variant="subtle">
              {t('management.epicMapping.help.howItWorks.description')}
            </HelpSection>

            <HelpSection title={t('management.epicMapping.help.applyNow.title')} variant="subtle">
              {t('management.epicMapping.help.applyNow.description')}
            </HelpSection>

            <HelpNote type="info">{t('management.epicMapping.help.note')}</HelpNote>
          </HelpPopover>
        }
        actions={
          (onNavigateToEpicLogin || onNavigateToSchedule) && (
            <>
              {onNavigateToEpicLogin && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={onNavigateToEpicLogin}
                  rightSection={<ExternalLink className="w-3.5 h-3.5" />}
                >
                  {t('management.epicMapping.configureLogin')}
                </Button>
              )}
              {onNavigateToSchedule && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={onNavigateToSchedule}
                  rightSection={<Calendar className="w-3.5 h-3.5" />}
                >
                  {t('management.epicMapping.viewSchedule')}
                </Button>
              )}
            </>
          )
        }
      />

      {/* Auth Status */}
      {
        <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
          <div className="flex items-center justify-between">
            <div className="flex-1">
              <div className="text-xs text-themed-muted space-y-1.5">
                <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                  <span className="opacity-60 text-left whitespace-nowrap">
                    {t('management.epicMapping.status.label')}
                  </span>
                  <span className="font-medium text-themed-primary flex items-center gap-1.5">
                    {isAuthenticated ? (
                      <>
                        <CheckCircle className="w-3.5 h-3.5 icon-green flex-shrink-0" />
                        {t('management.epicMapping.status.connectedAs', {
                          name: authStatus?.displayName || 'Epic User'
                        })}
                      </>
                    ) : (
                      <>
                        <XCircle className="w-3.5 h-3.5 icon-red flex-shrink-0" />
                        {t('management.epicMapping.status.notConnected')}
                      </>
                    )}
                  </span>
                </div>
                {isAuthenticated && (
                  <>
                    <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                      <span className="opacity-60 text-left whitespace-nowrap">
                        {t('management.epicMapping.status.gamesDiscovered')}
                      </span>
                      <span className="font-medium text-themed-primary">
                        {stats?.totalGames ?? authStatus?.gamesDiscovered ?? 0}
                      </span>
                    </div>
                    <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                      <span className="opacity-60 text-left whitespace-nowrap">
                        {t('management.epicMapping.status.cdnPatterns')}
                      </span>
                      <span className="font-medium text-themed-primary">
                        {stats?.cdnPatterns ?? 0}
                      </span>
                    </div>
                    {formattedLastCollection && (
                      <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                        <span className="opacity-60 text-left whitespace-nowrap">
                          {t('management.epicMapping.status.lastCollection')}
                        </span>
                        <span className="font-medium text-themed-primary">
                          {formattedLastCollection}
                        </span>
                      </div>
                    )}
                  </>
                )}
              </div>
            </div>
          </div>
        </div>
      }

      {/* Schedule redirect note */}
      {isAuthenticated && (
        <p className="text-xs text-themed-muted mb-4">
          {t('management.schedules.configuredInSchedules')}
        </p>
      )}

      {/* Not Authenticated Message */}
      {!isAuthenticated && (
        <div className="mb-4 text-xs text-themed-muted text-center py-2">
          {t('management.epicMapping.loginRequired')}
        </div>
      )}

      {/* Apply Now Button */}
      <div className="flex">
        <Button
          variant="filled"
          color="blue"
          onClick={handleRefresh}
          disabled={isEpicMappingFromNotification || mockMode || !isAdmin || !isAuthenticated}
          loading={isEpicMappingFromNotification}
          fullWidth
        >
          {isEpicMappingFromNotification
            ? t('management.epicMapping.buttons.resolving')
            : t('management.epicMapping.buttons.applyNow')}
        </Button>
      </div>
    </Card>
  );
};

export default EpicMappingManager;
