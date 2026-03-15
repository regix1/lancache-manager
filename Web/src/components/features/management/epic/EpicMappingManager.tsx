import React, { useState, useEffect, useCallback, useRef } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { ExternalLink, CheckCircle, XCircle, Clock } from 'lucide-react';
import { EpicIcon } from '@components/ui/EpicIcon';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { ManagerCardHeader } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import type {
  EpicMappingAuthStatus,
  EpicMappingStats,
  EpicScheduleStatus
} from '../../../../types';

interface EpicMappingManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onNavigateToEpicLogin?: () => void;
}

const EpicMappingManager: React.FC<EpicMappingManagerProps> = ({
  isAdmin,
  mockMode,
  onError,
  onNavigateToEpicLogin
}) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();

  const [authStatus, setAuthStatus] = useState<EpicMappingAuthStatus | null>(null);
  const [stats, setStats] = useState<EpicMappingStats | null>(null);
  const [schedule, setSchedule] = useState<EpicScheduleStatus | null>(null);
  const [localNextRefreshIn, setLocalNextRefreshIn] = useState<number | null>(null);
  const [resolving, setResolving] = useState(false);
  const resolveInProgressRef = useRef(false);

  const isAuthenticated = authStatus?.isAuthenticated ?? false;

  const loadStatus = useCallback(async () => {
    try {
      const [auth, statsData, scheduleData] = await Promise.all([
        ApiService.getEpicMappingAuthStatus(),
        ApiService.getEpicMappingStats(),
        ApiService.getEpicScheduleStatus()
      ]);
      setAuthStatus(auth);
      setStats(statsData);
      setSchedule(scheduleData);
    } catch {
      setAuthStatus({
        isAuthenticated: false,
        displayName: null,
        lastCollectionUtc: null,
        gamesDiscovered: 0
      });
      setStats(null);
      setSchedule(null);
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

  // Sync localNextRefreshIn when schedule data changes
  useEffect(() => {
    if (schedule?.nextRefreshIn !== undefined && schedule.nextRefreshIn > 0) {
      setLocalNextRefreshIn(schedule.nextRefreshIn);
    } else {
      setLocalNextRefreshIn(null);
    }
  }, [schedule?.nextRefreshIn]);

  // Countdown timer
  useEffect(() => {
    if (!localNextRefreshIn || schedule?.isProcessing || schedule?.refreshIntervalHours === 0) {
      return;
    }

    const timer = setInterval(() => {
      setLocalNextRefreshIn((prev) => {
        if (prev === null || prev <= 1) return null;
        return prev - 1;
      });
    }, 1000);

    return () => clearInterval(timer);
  }, [localNextRefreshIn, schedule?.isProcessing, schedule?.refreshIntervalHours]);

  const formattedLastCollection = useFormattedDateTime(authStatus?.lastCollectionUtc ?? null);
  const formattedLastRefresh = useFormattedDateTime(schedule?.lastRefreshTime ?? null);

  const formatNextRefresh = (): string => {
    if (!schedule || schedule.refreshIntervalHours === 0)
      return t('management.epicMapping.schedule.disabled');
    if (!localNextRefreshIn || localNextRefreshIn <= 0)
      return t('management.epicMapping.schedule.calculating');
    const hours = Math.floor(localNextRefreshIn / 3600);
    const minutes = Math.floor((localNextRefreshIn % 3600) / 60);
    const seconds = Math.floor(localNextRefreshIn % 60);
    if (hours > 0) return `${hours}h ${minutes}m`;
    if (minutes > 0) return `${minutes}m ${seconds}s`;
    return `${seconds}s`;
  };

  const handleRefresh = async () => {
    if (resolveInProgressRef.current) return;
    resolveInProgressRef.current = true;
    flushSync(() => setResolving(true));

    try {
      const result = await ApiService.startEpicRefresh();
      if (!result.started) {
        onError?.(result.message || 'A refresh is already in progress');
      }
      // Progress is tracked via SignalR notification bar — no inline success message needed
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to start Epic catalog refresh');
    } finally {
      setResolving(false);
      resolveInProgressRef.current = false;
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
          onNavigateToEpicLogin && (
            <Button
              variant="outline"
              size="sm"
              onClick={onNavigateToEpicLogin}
              rightSection={<ExternalLink className="w-3.5 h-3.5" />}
            >
              {t('management.epicMapping.configureLogin')}
            </Button>
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

      {/* Schedule Status */}
      {isAuthenticated && (
        <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
          <div className="flex flex-col lg:flex-row lg:items-start lg:justify-between gap-4">
            <div className="flex-1">
              <div className="flex items-center gap-2 mb-3">
                <Clock className="w-4 h-4 text-themed-primary" />
                <span className="text-sm font-medium text-themed-secondary">
                  {t('management.epicMapping.schedule.automaticRefresh')}
                </span>
              </div>
              <div className="text-xs text-themed-muted space-y-2 sm:space-y-1.5">
                <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                  <span className="opacity-60 text-left whitespace-nowrap">
                    {t('management.epicMapping.schedule.runsEvery')}
                  </span>
                  <span className="font-medium text-themed-primary">
                    {!schedule
                      ? t('common.loading')
                      : schedule.refreshIntervalHours === 0
                        ? t('management.epicMapping.schedule.disabled')
                        : t('management.epicMapping.intervals.hours', {
                            count: schedule.refreshIntervalHours
                          })}
                  </span>
                </div>
                <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                  <span className="opacity-60 text-left whitespace-nowrap">
                    {t('management.epicMapping.schedule.nextRun')}
                  </span>
                  <span className="font-medium text-themed-primary">
                    {!schedule || schedule.refreshIntervalHours === 0
                      ? t('management.epicMapping.schedule.disabled')
                      : formatNextRefresh()}
                  </span>
                </div>
                {schedule?.lastRefreshTime && (
                  <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
                    <span className="opacity-60 text-left whitespace-nowrap">
                      {t('management.epicMapping.schedule.lastRun')}
                    </span>
                    <span className="font-medium text-themed-primary">
                      {schedule.refreshIntervalHours === 0
                        ? t('management.epicMapping.schedule.disabled')
                        : formattedLastRefresh}
                    </span>
                  </div>
                )}
              </div>
            </div>
            <div className="flex flex-col gap-2 w-full lg:w-auto lg:min-w-[200px]">
              <EnhancedDropdown
                options={[
                  { value: '0', label: t('management.epicMapping.intervals.disabled') },
                  { value: '1', label: t('management.epicMapping.intervals.every1Hour') },
                  { value: '6', label: t('management.epicMapping.intervals.every6Hours') },
                  { value: '12', label: t('management.epicMapping.intervals.every12Hours') },
                  { value: '24', label: t('management.epicMapping.intervals.every24Hours') },
                  { value: '48', label: t('management.epicMapping.intervals.every2Days') },
                  { value: '168', label: t('management.epicMapping.intervals.weekly') }
                ]}
                value={schedule ? String(schedule.refreshIntervalHours) : '12'}
                onChange={async (value: string) => {
                  const newInterval = Number(value);
                  try {
                    await ApiService.setEpicRefreshInterval(newInterval);
                    loadStatus();
                  } catch (error) {
                    console.error('Failed to update Epic refresh interval:', error);
                  }
                }}
                disabled={!isAdmin || mockMode}
                className="w-full"
              />
            </div>
          </div>
        </div>
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
          disabled={resolving || mockMode || !isAdmin || !isAuthenticated}
          loading={resolving}
          fullWidth
        >
          {resolving
            ? t('management.epicMapping.buttons.resolving')
            : t('management.epicMapping.buttons.applyNow')}
        </Button>
      </div>
    </Card>
  );
};

export default EpicMappingManager;
