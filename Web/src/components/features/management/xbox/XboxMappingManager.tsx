import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { ExternalLink } from 'lucide-react';
import { XboxIcon } from '@components/ui/XboxIcon';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { ManagerCardHeader } from '@components/ui/ManagerCard';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import type { XboxMappingStats } from '../../../../types';

interface XboxMappingManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
  onSuccess?: (message: string) => void;
  onDataRefresh?: () => void;
  onNavigateToXboxLogin?: () => void;
}

/**
 * Xbox game mapping manager (Data section). Xbox titles resolve automatically during the Rust log
 * ingest, and the catalog is kept current by the scheduled XboxCatalogMappingService (key
 * `xboxMapping`) plus an on-login nudge. This card exposes the discovered catalog stats plus a manual
 * "Refresh Catalog" trigger that collects fresh titles + CDN patterns from any signed-in Xbox daemon
 * session and resolves unmatched downloads. Stats refresh via the XboxGameMappingsUpdated SignalR event.
 */
const XboxMappingManager: React.FC<XboxMappingManagerProps> = ({
  isAdmin,
  mockMode,
  onError,
  onNavigateToXboxLogin
}) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();

  // Derive Xbox mapping operation state from notifications (standardized pattern)
  const isXboxMappingFromNotification = useOperationBusy({ types: ['xbox_game_mapping'] });

  // The refresh-catalog call is a synchronous request that does not emit a progress notification,
  // so track its in-flight state locally to drive the button spinner.
  const [isRefreshing, setIsRefreshing] = useState(false);
  const isBusy = isRefreshing || isXboxMappingFromNotification;

  const [stats, setStats] = useState<XboxMappingStats | null>(null);

  const loadStats = useCallback(async () => {
    try {
      const statsData = await ApiService.getXboxMappingStats();
      setStats(statsData);
    } catch {
      setStats(null);
    }
  }, []);

  useEffect(() => {
    loadStats();
  }, [loadStats]);

  // Refresh on SignalR events
  useEffect(() => {
    const handleUpdate = () => {
      loadStats();
    };
    on('XboxGameMappingsUpdated', handleUpdate);
    on('XboxMappingProgress', handleUpdate);
    return () => {
      off('XboxGameMappingsUpdated', handleUpdate);
      off('XboxMappingProgress', handleUpdate);
    };
  }, [on, off, loadStats]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadStats();
    }
  }, [connectionState, loadStats]);

  const formattedLastUpdated = useFormattedDateTime(stats?.lastUpdatedUtc ?? null);

  const handleRefreshCatalog = async () => {
    if (isBusy) return;

    setIsRefreshing(true);
    try {
      // Collect fresh game titles + CDN patterns from any signed-in daemon session, then resolve.
      // When new games/patterns are found the backend emits XboxGameMappingsUpdated, which both
      // refreshes these stats and raises a completion toast.
      await ApiService.refreshXboxCatalog();
      await loadStats();
    } catch (err) {
      onError?.(err instanceof Error ? err.message : 'Failed to refresh Xbox catalog');
    } finally {
      setIsRefreshing(false);
    }
  };

  return (
    <Card>
      <ManagerCardHeader
        icon={XboxIcon}
        iconColor="green"
        title={t('management.xboxMapping.title')}
        subtitle={t('management.xboxMapping.subtitle')}
        helpContent={
          <HelpPopover position="left" width={320}>
            <HelpSection title={t('management.xboxMapping.help.howItWorks.title')} variant="subtle">
              {t('management.xboxMapping.help.howItWorks.description')}
            </HelpSection>

            <HelpSection title={t('management.xboxMapping.help.applyNow.title')} variant="subtle">
              {t('management.xboxMapping.help.applyNow.description')}
            </HelpSection>

            <HelpNote type="info">{t('management.xboxMapping.help.note')}</HelpNote>
          </HelpPopover>
        }
        actions={
          onNavigateToXboxLogin && (
            <Button
              variant="filled"
              color="blue"
              size="sm"
              onClick={onNavigateToXboxLogin}
              rightSection={<ExternalLink className="w-3.5 h-3.5" />}
            >
              {t('management.xboxMapping.configureLogin')}
            </Button>
          )
        }
      />

      {/* Stats */}
      <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
        <div className="text-xs text-themed-muted space-y-1.5">
          <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
            <span className="opacity-60 text-left whitespace-nowrap">
              {t('management.xboxMapping.status.gamesDiscovered')}
            </span>
            <span className="font-medium text-themed-primary tabular-nums">
              {stats?.totalGames ?? 0}
            </span>
          </div>
          {formattedLastUpdated && (
            <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
              <span className="opacity-60 text-left whitespace-nowrap">
                {t('management.xboxMapping.status.lastUpdated')}
              </span>
              <span className="font-medium text-themed-primary">{formattedLastUpdated}</span>
            </div>
          )}
        </div>
      </div>

      {/* Resolve Now Button */}
      <div className="flex">
        <Button
          variant="filled"
          color="blue"
          onClick={handleRefreshCatalog}
          disabled={isBusy || mockMode || !isAdmin}
          loading={isBusy}
          fullWidth
        >
          {isBusy
            ? t('management.xboxMapping.buttons.resolving')
            : t('management.xboxMapping.buttons.applyNow')}
        </Button>
      </div>
    </Card>
  );
};

export default XboxMappingManager;
