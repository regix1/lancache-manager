import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { HelpPopover, HelpSection, HelpNote } from '@components/ui/HelpPopover';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useOperationBusy } from '@/hooks/useOperationBusy';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import type { BlizzardMappingStats } from '../../../../types';

// Battle.net (Blizzard) mapping is fully static/public — Blizzard games are
// identified from TACT product codes embedded in the CDN download paths. Unlike
// Epic there is NO account login, API key, or "connected as" state. This card
// just lets an admin re-apply the catalog to existing unnamed Blizzard downloads.

interface BlizzardMappingManagerProps {
  isAdmin: boolean;
  mockMode: boolean;
  onError?: (message: string) => void;
}

const BlizzardMappingManager: React.FC<BlizzardMappingManagerProps> = ({
  isAdmin,
  mockMode,
  onError
}) => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();

  // Derive Blizzard mapping operation state from notifications (standardized pattern)
  const isBlizzardMappingFromNotification = useOperationBusy({ types: ['blizzard_game_mapping'] });

  const [stats, setStats] = useState<BlizzardMappingStats | null>(null);

  const loadStats = useCallback(async () => {
    try {
      const data = await ApiService.getBlizzardMappingStats();
      setStats(data);
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
    on('BlizzardGameMappingsUpdated', handleUpdate);
    return () => {
      off('BlizzardGameMappingsUpdated', handleUpdate);
    };
  }, [on, off, loadStats]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadStats();
    }
  }, [connectionState, loadStats]);

  const formattedLastApplied = useFormattedDateTime(stats?.lastAppliedUtc ?? null);

  const handleApply = async () => {
    if (isBlizzardMappingFromNotification) return;

    try {
      const result = await ApiService.resolveBlizzardDownloads();
      if (result.resolved === 0) {
        onError?.(result.message || t('management.blizzardMapping.noUnresolved'));
      }
      // Progress/completion is tracked via the SignalR notification bar
    } catch (err) {
      onError?.(
        err instanceof Error ? err.message : t('management.blizzardMapping.errors.applyFailed')
      );
    }
  };

  return (
    <Card>
      {/* Header: Blizzard icon + Title + HelpPopover */}
      <div className="flex items-center gap-3 mb-6">
        <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-blizzard-subtle)] text-[var(--theme-blizzard)] flex-shrink-0">
          <BlizzardIcon size={20} />
        </div>
        <div className="min-w-0 flex-1">
          <h3 className="text-lg font-semibold text-themed-primary truncate">
            {t('management.blizzardMapping.title')}
          </h3>
          <p className="text-xs text-themed-muted truncate">
            {t('management.blizzardMapping.subtitle')}
          </p>
        </div>
        <HelpPopover position="left" width={320}>
          <HelpSection
            title={t('management.blizzardMapping.help.howItWorks.title')}
            variant="subtle"
          >
            {t('management.blizzardMapping.help.howItWorks.description')}
          </HelpSection>

          <HelpSection title={t('management.blizzardMapping.help.applyNow.title')} variant="subtle">
            {t('management.blizzardMapping.help.applyNow.description')}
          </HelpSection>

          <HelpNote type="info">{t('management.blizzardMapping.help.note')}</HelpNote>
        </HelpPopover>
      </div>

      {/* Stats block (no auth/login — public catalog) */}
      <div className="mb-4 p-3 rounded-lg bg-themed-tertiary">
        <div className="text-xs text-themed-muted space-y-1.5">
          <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
            <span className="opacity-60 text-left whitespace-nowrap">
              {t('management.blizzardMapping.status.mappedProducts')}
            </span>
            <span className="font-medium text-themed-primary">{stats?.mappedProducts ?? 0}</span>
          </div>
          <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
            <span className="opacity-60 text-left whitespace-nowrap">
              {t('management.blizzardMapping.status.unnamedDownloads')}
            </span>
            <span className="font-medium text-themed-primary">{stats?.unnamedDownloads ?? 0}</span>
          </div>
          {formattedLastApplied && (
            <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-0.5 sm:gap-2">
              <span className="opacity-60 text-left whitespace-nowrap">
                {t('management.blizzardMapping.status.lastApplied')}
              </span>
              <span className="font-medium text-themed-primary">{formattedLastApplied}</span>
            </div>
          )}
        </div>
      </div>

      {/* No login required note */}
      <p className="text-xs text-themed-muted mb-4">{t('management.blizzardMapping.noLogin')}</p>

      {/* Apply Now Button */}
      <div className="flex">
        <Button
          variant="filled"
          color="blue"
          onClick={handleApply}
          disabled={isBlizzardMappingFromNotification || mockMode || !isAdmin}
          loading={isBlizzardMappingFromNotification}
          fullWidth
        >
          {isBlizzardMappingFromNotification
            ? t('management.blizzardMapping.buttons.resolving')
            : t('management.blizzardMapping.buttons.applyNow')}
        </Button>
      </div>
    </Card>
  );
};

export default BlizzardMappingManager;
