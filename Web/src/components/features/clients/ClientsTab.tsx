import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { formatBytes, formatPercent } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { Card } from '@components/ui/Card';
import { CacheInfoTooltip, Tooltip } from '@components/ui/Tooltip';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { EmptyState } from '@components/ui/ManagerCard';
import { Users } from 'lucide-react';
import type { ClientStat, SortOption, SortDirection } from './types';
import '@components/features/management/managementSectionContent.css';
import '@/styles/features/clients.css';

interface ClientListItemProps {
  client: ClientStat;
}

/**
 * One dense table row (CSS grid): client identity plus six figure columns.
 * Under 768px the same DOM reflows into a labeled two-column stack via the
 * cells' data-label attributes — one component, no forked tree, no per-client
 * readout wells.
 */
const ClientListItem: React.FC<ClientListItemProps> = ({ client }) => {
  const { t } = useTranslation();
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);
  const displayLabel = client.displayName || client.clientIp;
  const showGroupCount = !!(client.isGrouped && (client.groupMemberIps?.length ?? 0) > 1);
  const ipTooltip =
    client.isGrouped && client.groupMemberIps
      ? t('clients.tooltips.groupIps', { ips: client.groupMemberIps.join(', ') })
      : client.displayName
        ? t('clients.tooltips.singleIp', { ip: client.clientIp })
        : undefined;
  const hitRateTone = client.cacheHitPercent > 50 ? 'is-success' : 'is-warning';

  return (
    <div className="clients-grid">
      <div className="clients-cell clients-cell--client">
        {client.isGrouped && <Users className="w-4 h-4 text-themed-muted flex-shrink-0" />}
        {ipTooltip ? (
          <Tooltip content={ipTooltip}>
            <span className="cursor-help border-b border-dashed border-themed-muted truncate">
              {displayLabel}
            </span>
          </Tooltip>
        ) : (
          <span className="truncate">{displayLabel}</span>
        )}
        {showGroupCount && (
          <span
            className="themed-badge status-badge-neutral badge-count"
            aria-label={t('clients.groupCount', { count: client.groupMemberIps!.length })}
          >
            {client.groupMemberIps!.length}
          </span>
        )}
      </div>
      <div
        className="clients-cell clients-cell--num"
        data-label={t('clients.table.totalDownloads')}
      >
        {client.totalDownloads}
      </div>
      <div className="clients-cell clients-cell--num" data-label={t('clients.table.totalData')}>
        {formatBytes(client.totalBytes)}
      </div>
      <div
        className="clients-cell clients-cell--num cache-hit"
        data-label={t('clients.table.cacheHits')}
      >
        {formatBytes(client.totalCacheHitBytes)}
      </div>
      <div
        className="clients-cell clients-cell--num cache-miss"
        data-label={t('clients.table.cacheMisses')}
      >
        {formatBytes(client.totalCacheMissBytes)}
      </div>
      <div
        className={`clients-cell clients-cell--num clients-hit ${hitRateTone}`}
        data-label={t('clients.table.hitRate')}
      >
        {formatPercent(client.cacheHitPercent)}
      </div>
      <div
        className="clients-cell clients-cell--num clients-cell--lg clients-cell--muted"
        data-label={t('clients.table.lastActivity')}
      >
        {formattedLastActivity}
      </div>
    </div>
  );
};

/** Stable identity for a sorted row: the group as a whole when grouped (survives
 *  re-sorting), otherwise the client's own IP. */
const getClientKey = (client: ClientStat): string =>
  client.isGrouped ? `group-${client.groupId ?? client.clientIp}` : client.clientIp;

const ClientsTab: React.FC = () => {
  const { t } = useTranslation();
  const { clientStats, loading } = useStats();
  const [sortBy, setSortBy] = useState<SortOption>('totalData');
  const [sortDirection, setSortDirection] = useState<SortDirection>('desc');
  const sortOptions = [
    { value: 'totalData', label: t('clients.sort.totalData') },
    { value: 'downloads', label: t('clients.sort.totalDownloads') },
    { value: 'hits', label: t('clients.sort.cacheHits') },
    { value: 'misses', label: t('clients.sort.cacheMisses') },
    { value: 'hitRate', label: t('clients.sort.hitRate') },
    { value: 'lastActivity', label: t('clients.sort.lastActivity') },
    { value: 'ip', label: t('clients.sort.clientName') }
  ];

  const directionOptions = [
    { value: 'desc', label: t('clients.sort.descending') },
    { value: 'asc', label: t('clients.sort.ascending') }
  ];

  const sortedClients = useMemo(() => {
    const sorted = [...clientStats];
    const multiplier = sortDirection === 'desc' ? -1 : 1;

    sorted.sort((a, b) => {
      switch (sortBy) {
        case 'ip': {
          // Sort by display name (nickname if available, otherwise IP)
          const aName = a.displayName || a.clientIp;
          const bName = b.displayName || b.clientIp;
          return multiplier * aName.localeCompare(bName);
        }
        case 'downloads':
          return multiplier * (a.totalDownloads - b.totalDownloads);
        case 'totalData':
          return multiplier * (a.totalBytes - b.totalBytes);
        case 'hits':
          return multiplier * (a.totalCacheHitBytes - b.totalCacheHitBytes);
        case 'misses':
          return multiplier * (a.totalCacheMissBytes - b.totalCacheMissBytes);
        case 'hitRate':
          return multiplier * (a.cacheHitPercent - b.cacheHitPercent);
        case 'lastActivity':
          return (
            multiplier *
            (new Date(a.lastActivityUtc).getTime() - new Date(b.lastActivityUtc).getTime())
          );
        default:
          return 0;
      }
    });

    return sorted;
  }, [clientStats, sortBy, sortDirection]);

  return (
    <div className="space-y-6 animate-fadeIn">
      <h2 className="text-xl sm:text-2xl font-bold text-themed-primary tracking-tight hidden md:block">
        {t('clients.title')}
      </h2>

      <Card>
        <div className="mgmt-toolbar mb-4">
          <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
            {t('clients.subtitle')}
            <CacheInfoTooltip />
          </h3>
          <div className="flex items-center gap-2 flex-wrap">
            <EnhancedDropdown
              options={sortOptions}
              value={sortBy}
              onChange={(value) => setSortBy(value as SortOption)}
              prefix={t('clients.sort.prefix')}
              className="clients-sort-field"
              size="md"
              cleanStyle
            />
            <EnhancedDropdown
              options={directionOptions}
              value={sortDirection}
              onChange={(value) => setSortDirection(value as SortDirection)}
              className="clients-sort-direction"
              size="md"
              cleanStyle
            />
          </div>
        </div>

        {loading ? (
          <div className="well-surface clients-well divided-list" aria-hidden="true">
            {Array.from({ length: 8 }, (_, i) => (
              <div key={i} className="clients-grid">
                <div className="clients-cell clients-cell--client">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--title" />
                </div>
                <div className="clients-cell clients-cell--num">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--value" />
                </div>
                <div className="clients-cell clients-cell--num">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--value" />
                </div>
                <div className="clients-cell clients-cell--num">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--value" />
                </div>
                <div className="clients-cell clients-cell--num">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--value" />
                </div>
                <div className="clients-cell clients-cell--num">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--value" />
                </div>
                <div className="clients-cell clients-cell--num clients-cell--lg">
                  <div className="clients-skeleton-line skeleton-shimmer clients-skeleton-line--value" />
                </div>
              </div>
            ))}
          </div>
        ) : sortedClients.length > 0 ? (
          <div className="well-surface clients-well divided-list">
            <div className="clients-grid clients-grid--header">
              <div className="clients-cell clients-cell--client">{t('clients.table.client')}</div>
              <div className="clients-cell clients-cell--num">
                {t('clients.table.totalDownloads')}
              </div>
              <div className="clients-cell clients-cell--num">{t('clients.table.totalData')}</div>
              <div className="clients-cell clients-cell--num">{t('clients.table.cacheHits')}</div>
              <div className="clients-cell clients-cell--num">{t('clients.table.cacheMisses')}</div>
              <div className="clients-cell clients-cell--num">{t('clients.table.hitRate')}</div>
              <div className="clients-cell clients-cell--num clients-cell--lg">
                {t('clients.table.lastActivity')}
              </div>
            </div>
            {sortedClients.map((client) => (
              <ClientListItem key={getClientKey(client)} client={client} />
            ))}
          </div>
        ) : (
          <div className="well-surface dash-well p-3">
            <EmptyState variant="panel" icon={Users} title={t('clients.empty')} />
          </div>
        )}
      </Card>
    </div>
  );
};

export default ClientsTab;
