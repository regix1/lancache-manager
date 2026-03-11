import React, { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Loader2, Zap, Search } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { Button } from '@components/ui/Button';
import { useSignalR } from '@contexts/SignalRContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import ApiService from '@services/api.service';
import { type AuthMode } from '@services/auth.service';
import type { EpicGameMappingDto, EpicMappingStats } from '../../../../types';
import './EpicGameMappings.css';

interface EpicGameMappingsProps {
  authMode: AuthMode;
}

/** Sub-component for rendering a single mapping row, so useFormattedDateTime hook can be called per-row */
const MappingRow: React.FC<{ mapping: EpicGameMappingDto }> = ({ mapping }) => {
  const formattedDiscovered = useFormattedDateTime(mapping.discoveredAtUtc);
  const formattedLastSeen = useFormattedDateTime(mapping.lastSeenAtUtc);

  return (
    <tr>
      <td className="epic-mappings-image-cell">
        {mapping.imageUrl ? (
          <img
            src={mapping.imageUrl}
            alt={mapping.name}
            className="epic-mappings-thumbnail"
            onError={(e: React.SyntheticEvent<HTMLImageElement>) => {
              e.currentTarget.style.display = 'none';
            }}
          />
        ) : (
          <span className="epic-mappings-no-image">—</span>
        )}
      </td>
      <td className="epic-mappings-name-cell">{mapping.name}</td>
      <td className="epic-mappings-appid-cell">{mapping.appId}</td>
      <td>{formattedDiscovered}</td>
      <td>{formattedLastSeen}</td>
      <td className="epic-mappings-url-cell">
        {mapping.imageUrl ? (
          <a
            href={mapping.imageUrl}
            target="_blank"
            rel="noopener noreferrer"
            className="epic-mappings-url-link"
          >
            {mapping.imageUrl}
          </a>
        ) : (
          <span className="epic-mappings-no-image">None</span>
        )}
      </td>
    </tr>
  );
};

/** Sub-component for the stats last-updated display */
const LastUpdatedLabel: React.FC<{ dateStr: string; label: string }> = ({ dateStr, label }) => {
  const formatted = useFormattedDateTime(dateStr);
  return (
    <span className="epic-mappings-last-updated">
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
    } else if (value.length === 0) {
      loadData();
    }
  };

  const filteredMappings = searchQuery.length < 2 ? mappings : mappings;

  if (loading) {
    return (
      <Card>
        <div className="epic-mappings-loading">
          <Loader2 className="epic-mappings-spinner" />
        </div>
      </Card>
    );
  }

  return (
    <Card>
      <div className="epic-mappings-container">
        {/* Header */}
        <div className="epic-mappings-header">
          <div>
            <h4 className="epic-mappings-title">
              {t('management.sections.integrations.epicGameMappings.title')}
            </h4>
            <p className="epic-mappings-subtitle">
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
        {error && <div className="epic-mappings-error">{error}</div>}

        {/* Empty State */}
        {mappings.length === 0 && !searchQuery && (
          <p className="epic-mappings-empty">
            {t('management.sections.integrations.epicGameMappings.noGames')}
          </p>
        )}

        {/* Search and Table */}
        {(mappings.length > 0 || searchQuery) && (
          <>
            {/* Search */}
            <div className="epic-mappings-search">
              <Search size={14} className="epic-mappings-search-icon" />
              <input
                type="text"
                value={searchQuery}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => handleSearch(e.target.value)}
                placeholder={t('management.sections.integrations.epicGameMappings.search')}
                className="epic-mappings-search-input"
              />
            </div>

            {/* Table */}
            {filteredMappings.length === 0 ? (
              <p className="epic-mappings-no-results">
                {t('management.sections.integrations.epicGameMappings.noResults')}
              </p>
            ) : (
              <div className="epic-mappings-table-wrapper">
                <table className="epic-mappings-table">
                  <thead>
                    <tr>
                      <th className="epic-mappings-image-header">
                        {t('management.sections.integrations.epicGameMappings.image')}
                      </th>
                      <th>{t('management.sections.integrations.epicGameMappings.name')}</th>
                      <th>{t('management.sections.integrations.epicGameMappings.appId')}</th>
                      <th>{t('management.sections.integrations.epicGameMappings.discovered')}</th>
                      <th>{t('management.sections.integrations.epicGameMappings.lastSeen')}</th>
                      <th>{t('management.sections.integrations.epicGameMappings.imageUrl')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {filteredMappings.map((mapping: EpicGameMappingDto) => (
                      <MappingRow key={mapping.appId} mapping={mapping} />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}

        {/* Apply Now Button */}
        {isAdmin && (
          <div className="epic-mappings-actions">
            <Button
              variant="filled"
              color="blue"
              leftSection={
                resolving ? (
                  <Loader2 className="w-4 h-4 animate-spin" />
                ) : (
                  <Zap className="w-4 h-4" />
                )
              }
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
