import React, { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Gamepad2, Search } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { ManagerCardHeader } from '@components/ui/ManagerCard';
import { DataTable, type DataTableColumn } from '@components/ui/DataTable';
import { Tooltip } from '@components/ui/Tooltip';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import type { EpicGameMappingDto, EpicMappingStats } from '../../../../types';

/** Returns a badge CSS class based on the discovery source */
const getSourceBadgeClass = (source: string): string => {
  const key = source.toLowerCase();
  const classMap: Record<string, string> = {
    'mapping-login': 'source-badge-mapping-login',
    'prefill-login': 'source-badge-prefill-login',
    'free-games': 'source-badge-free-games',
    library: 'source-badge-library',
    wishlist: 'source-badge-wishlist',
    store: 'source-badge-store',
    manual: 'source-badge-manual',
    daemon: 'source-badge-daemon'
  };
  return classMap[key] ?? 'source-badge-default';
};

/** Returns the i18n key suffix for source description tooltip */
const getSourceDescriptionKey = (source: string): string => {
  const key = source.toLowerCase();
  const validKeys = [
    'mapping-login',
    'prefill-login',
    'free-games',
    'library',
    'wishlist',
    'store',
    'manual',
    'daemon'
  ];
  return validKeys.includes(key) ? key : 'unknown';
};

/** Wrapper component so useFormattedDateTime hook can be called per-row */
const FormattedDateCell: React.FC<{ dateStr: string }> = ({ dateStr }) => {
  const formatted = useFormattedDateTime(dateStr);
  return (
    <span className="block truncate text-xs text-themed-secondary whitespace-nowrap">
      {formatted}
    </span>
  );
};

/** Source badge with tooltip explaining discovery method */
const SourceBadgeCell: React.FC<{ source: string }> = ({ source }) => {
  const { t } = useTranslation();
  return (
    <Tooltip
      content={t(
        `management.sections.integrations.epicGameMappings.sourceDescriptions.${getSourceDescriptionKey(source)}`
      )}
      position="top"
    >
      <span className={`epic-source-badge ${getSourceBadgeClass(source)}`}>{source}</span>
    </Tooltip>
  );
};

const EpicGameMappings: React.FC = () => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();

  const [mappings, setMappings] = useState<EpicGameMappingDto[]>([]);
  const [stats, setStats] = useState<EpicMappingStats | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [error, setError] = useState<string | null>(null);
  const searchTimeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const loadData = useCallback(async () => {
    try {
      setError(null);
      const [mappingsData, statsData] = await Promise.all([
        ApiService.getEpicGameMappings(),
        ApiService.getEpicMappingStats()
      ]);
      setMappings(mappingsData);
      setStats(statsData);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load Epic game mappings');
    }
  }, []);

  useEffect(() => {
    loadData();
  }, [loadData]);

  // Listen for SignalR updates
  useEffect(() => {
    const handleUpdate = () => {
      loadData();
    };

    on('EpicGameMappingsUpdated', handleUpdate);
    return () => {
      off('EpicGameMappingsUpdated', handleUpdate);
    };
  }, [on, off, loadData]);

  // Refresh data when SignalR reconnects (catches events missed during disconnect)
  useEffect(() => {
    if (connectionState === 'connected') {
      loadData();
    }
  }, [connectionState, loadData]);

  const handleSearch = (value: string) => {
    setSearchQuery(value);
    if (searchTimeoutRef.current) {
      clearTimeout(searchTimeoutRef.current);
    }

    if (value.length >= 2) {
      searchTimeoutRef.current = setTimeout(async () => {
        try {
          const results = await ApiService.searchEpicGames(value);
          setMappings(results);
        } catch {
          // Silently fail search, keep current results
        }
      }, 300);
    } else if (value.length < 2) {
      loadData();
    }
  };

  // Define columns for DataTable (resizable with pixel defaults)
  const columns: DataTableColumn<EpicGameMappingDto>[] = useMemo(
    () => [
      {
        key: 'image',
        header: '',
        defaultWidth: 52,
        minWidth: 48,
        align: 'center' as const,
        render: (mapping: EpicGameMappingDto) =>
          mapping.imageUrl ? (
            <img
              src={mapping.imageUrl}
              alt={mapping.name}
              className="w-10 h-10 object-cover rounded align-middle"
              onError={(e: React.SyntheticEvent<HTMLImageElement>) => {
                e.currentTarget.classList.add('hidden');
              }}
            />
          ) : (
            <div className="w-10 h-10 rounded flex items-center justify-center bg-[var(--theme-bg-tertiary)]">
              <Gamepad2 className="w-4 h-4 text-themed-muted" />
            </div>
          )
      },
      {
        key: 'name',
        header: t('management.sections.integrations.epicGameMappings.name'),
        defaultWidth: 200,
        minWidth: 100,
        render: (mapping: EpicGameMappingDto) => (
          <span
            className="block truncate text-xs font-medium text-themed-primary"
            title={mapping.name}
          >
            {mapping.name}
          </span>
        )
      },
      {
        key: 'appId',
        header: t('management.sections.integrations.epicGameMappings.appId'),
        defaultWidth: 140,
        minWidth: 80,
        render: (mapping: EpicGameMappingDto) => (
          <span
            className="block truncate font-mono text-xs text-themed-secondary"
            title={mapping.appId}
          >
            {mapping.appId}
          </span>
        )
      },
      {
        key: 'source',
        header: t('management.sections.integrations.epicGameMappings.source'),
        defaultWidth: 130,
        minWidth: 80,
        align: 'center' as const,
        render: (mapping: EpicGameMappingDto) => <SourceBadgeCell source={mapping.source} />
      },
      {
        key: 'discovered',
        header: t('management.sections.integrations.epicGameMappings.discovered'),
        defaultWidth: 150,
        minWidth: 100,
        render: (mapping: EpicGameMappingDto) => (
          <FormattedDateCell dateStr={mapping.discoveredAtUtc} />
        )
      },
      {
        key: 'lastSeen',
        header: t('management.sections.integrations.epicGameMappings.lastSeen'),
        defaultWidth: 150,
        minWidth: 100,
        render: (mapping: EpicGameMappingDto) => (
          <FormattedDateCell dateStr={mapping.lastSeenAtUtc} />
        )
      }
    ],
    [t]
  );

  return (
    <Card>
      <div className="flex flex-col gap-4">
        {/* Card Header */}
        <ManagerCardHeader
          icon={Gamepad2}
          iconColor="purple"
          title={t('management.sections.integrations.epicGameMappings.title')}
          subtitle={
            stats && stats.totalGames > 0
              ? t('management.sections.integrations.epicGameMappings.gamesDiscovered', {
                  count: stats.totalGames
                })
              : t('management.sections.integrations.epicGameMappings.description')
          }
        />

        {/* Error / Info Message */}
        {error && <div className="p-4 text-center text-[var(--theme-error)]">{error}</div>}

        {/* Empty State */}
        {mappings.length === 0 && !searchQuery && (
          <p className="text-xs text-themed-secondary text-center py-6">
            {t('management.sections.integrations.epicGameMappings.noGames')}
          </p>
        )}

        {/* Search and Table */}
        {(mappings.length > 0 || searchQuery) && (
          <>
            {/* Search */}
            <div className="relative">
              <Search
                size={14}
                className="absolute left-3 top-1/2 -translate-y-1/2 text-themed-muted pointer-events-none"
              />
              <input
                type="text"
                value={searchQuery}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => handleSearch(e.target.value)}
                placeholder={t('management.sections.integrations.epicGameMappings.search')}
                className="w-full py-2 pl-8 pr-3 border border-[var(--theme-border)] rounded-lg bg-themed-secondary text-themed-primary text-xs outline-none focus:border-[var(--theme-primary)]"
              />
            </div>

            {/* DataTable */}
            {mappings.length === 0 ? (
              <p className="text-xs text-themed-secondary text-center py-4">
                {t('management.sections.integrations.epicGameMappings.noResults')}
              </p>
            ) : (
              <DataTable<EpicGameMappingDto>
                columns={columns}
                data={mappings}
                keyExtractor={(mapping: EpicGameMappingDto) => mapping.appId}
                maxHeight="400px"
                accentColor={() => 'var(--theme-epic)'}
                resizable
                storageKey="epic-game-mappings-column-widths"
                compact
              />
            )}
          </>
        )}
      </div>
    </Card>
  );
};

export default EpicGameMappings;
