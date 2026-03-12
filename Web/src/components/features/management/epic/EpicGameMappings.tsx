import React, { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2, Gamepad2, Search, ExternalLink } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';

import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { useSignalR } from '@contexts/SignalRContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import type { EpicGameMappingDto, EpicMappingStats } from '../../../../types';

interface EpicGameMappingsProps {
  authMode: AuthMode;
}

/** Sub-component for rendering a single mapping row, so useFormattedDateTime hook can be called per-row */
const MappingRow: React.FC<{ mapping: EpicGameMappingDto }> = ({ mapping }) => {
  const formattedDiscovered = useFormattedDateTime(mapping.discoveredAtUtc);
  const formattedLastSeen = useFormattedDateTime(mapping.lastSeenAtUtc);

  return (
    <tr className="hover:bg-[var(--theme-bg-hover,var(--theme-bg-secondary))]">
      <td className="px-2 py-1 text-themed-primary border-b border-[var(--theme-border)] w-12">
        {mapping.imageUrl ? (
          <img
            src={mapping.imageUrl}
            alt={mapping.name}
            className="w-10 h-[22px] object-cover rounded-sm align-middle"
            onError={(e: React.SyntheticEvent<HTMLImageElement>) => {
              e.currentTarget.style.display = 'none';
            }}
          />
        ) : (
          <span className="text-themed-muted text-xs">—</span>
        )}
      </td>
      <td className="px-3 py-1.5 text-themed-primary border-b border-[var(--theme-border)] font-medium truncate">
        {mapping.name}
      </td>
      <td className="px-3 py-1.5 border-b border-[var(--theme-border)] font-mono text-xs text-themed-secondary truncate">
        {mapping.appId}
      </td>
      <td className="px-3 py-1.5 border-b border-[var(--theme-border)]">
        <span className="inline-block px-2 py-0.5 text-[10px] font-medium rounded-md bg-themed-secondary border border-themed-primary text-themed-secondary">
          {mapping.source}
        </span>
      </td>
      <td className="px-3 py-1.5 text-themed-primary border-b border-[var(--theme-border)] whitespace-nowrap">
        {formattedDiscovered}
      </td>
      <td className="px-3 py-1.5 text-themed-primary border-b border-[var(--theme-border)] whitespace-nowrap">
        {formattedLastSeen}
      </td>
      <td className="px-3 py-1.5 border-b border-[var(--theme-border)] text-center">
        {mapping.imageUrl ? (
          <a
            href={mapping.imageUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 px-2 py-1 text-[10px] font-medium rounded-md bg-themed-secondary border border-themed-primary text-themed-secondary no-underline hover:bg-[var(--theme-bg-hover)] transition-colors"
          >
            <ExternalLink className="w-3 h-3" />
            View
          </a>
        ) : (
          <span className="text-themed-muted text-xs">—</span>
        )}
      </td>
    </tr>
  );
};

/** Sub-component for the stats last-updated display */
const LastUpdatedLabel: React.FC<{ dateStr: string; label: string }> = ({ dateStr, label }) => {
  const formatted = useFormattedDateTime(dateStr);
  return (
    <span className="text-themed-muted">
      {' '}
      &middot; {label}: {formatted}
    </span>
  );
};

const EpicGameMappings: React.FC<EpicGameMappingsProps> = ({ authMode }) => {
  const { t } = useTranslation();
  const { on, off } = useSignalR();

  const [mappings, setMappings] = useState<EpicGameMappingDto[]>([]);
  const [stats, setStats] = useState<EpicMappingStats | null>(null);
  const [loading, setLoading] = useState(true);
  const [resolving, setResolving] = useState(false);
  const [searchQuery, setSearchQuery] = useState('');
  const [error, setError] = useState<string | null>(null);
  const searchTimeoutRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const isAdmin = authMode === 'authenticated';

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
    } finally {
      setLoading(false);
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

  const handleResolve = async () => {
    setResolving(true);
    setError(null);
    try {
      const result = await ApiService.resolveEpicDownloads();
      // Reload data to show updated state
      await loadData();
      if (result.resolved === 0) {
        setError(t('management.sections.data.epicResolve.noUnresolved'));
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to resolve Epic downloads');
    } finally {
      setResolving(false);
    }
  };

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

  if (loading) {
    return (
      <Card>
        <div className="flex items-center justify-center py-8">
          <Loader2 className="w-5 h-5 animate-spin text-themed-secondary" />
        </div>
      </Card>
    );
  }

  return (
    <Card>
      <div className="flex flex-col gap-4">
        {/* Card Header */}
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-lg flex items-center justify-center icon-bg-purple">
            <Gamepad2 className="w-5 h-5 icon-purple" />
          </div>
          <div>
            <h3 className="text-lg font-semibold text-themed-primary">
              {t('management.sections.integrations.epicGameMappings.title')}
            </h3>
            <p className="text-xs text-themed-muted">
              {stats && stats.totalGames > 0
                ? t('management.sections.integrations.epicGameMappings.gamesDiscovered', {
                    count: stats.totalGames
                  })
                : t('management.sections.integrations.epicGameMappings.description')}
              {stats?.lastUpdatedUtc && (
                <LastUpdatedLabel
                  dateStr={stats.lastUpdatedUtc}
                  label={t('management.sections.integrations.epicGameMappings.lastUpdated')}
                />
              )}
            </p>
          </div>
        </div>

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

            {/* Table */}
            {mappings.length === 0 ? (
              <p className="text-xs text-themed-secondary text-center py-4">
                {t('management.sections.integrations.epicGameMappings.noResults')}
              </p>
            ) : (
              <div className="rounded-lg border border-[var(--theme-border)] overflow-hidden">
                <CustomScrollbar maxHeight="400px" className="!rounded-lg">
                  <table className="w-full text-xs border-collapse table-fixed">
                    <colgroup>
                      <col className="w-12" />
                      <col />
                      <col />
                      <col />
                      <col />
                      <col />
                      <col className="w-[4.5rem]" />
                    </colgroup>
                    <thead>
                      <tr>
                        <th className="text-left px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.image')}
                        </th>
                        <th className="text-left px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.name')}
                        </th>
                        <th className="text-left px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.appId')}
                        </th>
                        <th className="text-left px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.source')}
                        </th>
                        <th className="text-left px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.discovered')}
                        </th>
                        <th className="text-left px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.lastSeen')}
                        </th>
                        <th className="text-center px-3 py-2 font-semibold text-themed-secondary border-b border-[var(--theme-border)] sticky top-0 bg-[var(--theme-bg-primary)]">
                          {t('management.sections.integrations.epicGameMappings.imageUrl')}
                        </th>
                      </tr>
                    </thead>
                    <tbody>
                      {mappings.map((mapping: EpicGameMappingDto) => (
                        <MappingRow key={mapping.appId} mapping={mapping} />
                      ))}
                    </tbody>
                  </table>
                </CustomScrollbar>
              </div>
            )}
          </>
        )}

        {/* Apply Now Button */}
        {isAdmin && (
          <div className="pt-2 border-t border-[var(--theme-border)]">
            <Button
              variant="filled"
              color="blue"
              onClick={handleResolve}
              disabled={resolving}
              loading={resolving}
              fullWidth
            >
              {resolving
                ? t('management.sections.data.epicResolve.resolving')
                : t('management.sections.data.epicResolve.applyNow')}
            </Button>
          </div>
        )}
      </div>
    </Card>
  );
};

export default EpicGameMappings;
