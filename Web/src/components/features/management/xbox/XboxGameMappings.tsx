import React, { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Gamepad2, Search } from 'lucide-react';
import { DataTable, type DataTableColumn } from '@components/ui/DataTable';
import { AccordionSection } from '@components/ui/AccordionSection';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import type { XboxGameMappingDto, XboxMappingStats } from '../../../../types';

/** Wrapper component so useFormattedDateTime hook can be called per-row */
const FormattedDateCell: React.FC<{ dateStr: string }> = ({ dateStr }) => {
  const formatted = useFormattedDateTime(dateStr);
  return (
    <span className="block truncate text-xs text-themed-secondary whitespace-nowrap">
      {formatted}
    </span>
  );
};

/**
 * Xbox library catalog: the cumulative, SHARED set of Microsoft Store titles discovered through
 * user prefill logins, wrapped in a collapsible AccordionSection (mission: each service's games
 * list is a dropdown). Unlike Epic there is no per-mapping discovery source - the resolution path
 * is the Rust ingest pass, so the table shows ProductId + title + discovery timestamps only.
 */
const XboxGameMappings: React.FC = () => {
  const { t } = useTranslation();
  const { on, off, connectionState } = useSignalR();

  const [mappings, setMappings] = useState<XboxGameMappingDto[]>([]);
  const [stats, setStats] = useState<XboxMappingStats | null>(null);
  const [searchQuery, setSearchQuery] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState(false);
  const searchTimeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const loadData = useCallback(async () => {
    try {
      setError(null);
      const [mappingsData, statsData] = await Promise.all([
        ApiService.getXboxGameMappings(),
        ApiService.getXboxMappingStats()
      ]);
      setMappings(mappingsData);
      setStats(statsData);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load Xbox game mappings');
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

    on('XboxGameMappingsUpdated', handleUpdate);
    return () => {
      off('XboxGameMappingsUpdated', handleUpdate);
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
          const results = await ApiService.searchXboxGames(value);
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
  const columns: DataTableColumn<XboxGameMappingDto>[] = useMemo(
    () => [
      {
        key: 'image',
        header: '',
        defaultWidth: 52,
        minWidth: 48,
        align: 'center' as const,
        render: (mapping: XboxGameMappingDto) =>
          mapping.imageUrl ? (
            <img
              src={mapping.imageUrl}
              alt={mapping.title}
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
        key: 'title',
        header: t('management.sections.integrations.xboxGameMappings.name'),
        defaultWidth: 280,
        minWidth: 140,
        flexible: true,
        render: (mapping: XboxGameMappingDto) => (
          <span
            className="block truncate text-xs font-medium text-themed-primary"
            title={mapping.title}
          >
            {mapping.title}
          </span>
        )
      },
      {
        key: 'productId',
        header: t('management.sections.integrations.xboxGameMappings.productId'),
        defaultWidth: 200,
        minWidth: 100,
        render: (mapping: XboxGameMappingDto) => (
          <span
            className="block truncate font-mono text-xs text-themed-secondary"
            title={mapping.productId}
          >
            {mapping.productId}
          </span>
        )
      },
      {
        key: 'discovered',
        header: t('management.sections.integrations.xboxGameMappings.discovered'),
        defaultWidth: 150,
        minWidth: 110,
        render: (mapping: XboxGameMappingDto) => (
          <FormattedDateCell dateStr={mapping.discoveredAtUtc} />
        )
      },
      {
        key: 'lastSeen',
        header: t('management.sections.integrations.xboxGameMappings.lastSeen'),
        defaultWidth: 150,
        minWidth: 110,
        render: (mapping: XboxGameMappingDto) => (
          <FormattedDateCell dateStr={mapping.lastSeenAtUtc} />
        )
      }
    ],
    [t]
  );

  return (
    <AccordionSection
      title={t('management.sections.integrations.xboxGameMappings.title')}
      icon={Gamepad2}
      iconColor="var(--theme-xbox)"
      count={stats?.totalGames}
      isExpanded={expanded}
      onToggle={() => setExpanded((prev) => !prev)}
    >
      <div className="space-y-3">
        {/* Description */}
        <p className="text-xs text-themed-muted">
          {stats && stats.totalGames > 0
            ? t('management.sections.integrations.xboxGameMappings.gamesInLibrary', {
                count: stats.totalGames
              })
            : t('management.sections.integrations.xboxGameMappings.description')}
        </p>

        {/* Error Message */}
        {error && <div className="p-4 text-center text-[var(--theme-error)]">{error}</div>}

        {/* Empty State */}
        {mappings.length === 0 && !searchQuery && (
          <p className="text-xs text-themed-secondary text-center py-6">
            {t('management.sections.integrations.xboxGameMappings.noGames')}
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
                placeholder={t('management.sections.integrations.xboxGameMappings.search')}
                className="w-full py-2 pl-8 pr-3 border border-[var(--theme-border)] rounded-lg bg-themed-secondary text-themed-primary text-xs outline-none focus:border-[var(--theme-primary)]"
              />
            </div>

            {/* DataTable */}
            {mappings.length === 0 ? (
              <p className="text-xs text-themed-secondary text-center py-4">
                {t('management.sections.integrations.xboxGameMappings.noResults')}
              </p>
            ) : (
              <DataTable<XboxGameMappingDto>
                columns={columns}
                data={mappings}
                keyExtractor={(mapping: XboxGameMappingDto) => mapping.productId}
                maxHeight="400px"
                accentColor={() => 'var(--theme-xbox)'}
                resizable
                storageKey="xbox-game-mappings-column-widths-v1"
                compact
              />
            )}
          </>
        )}
      </div>
    </AccordionSection>
  );
};

export default XboxGameMappings;
