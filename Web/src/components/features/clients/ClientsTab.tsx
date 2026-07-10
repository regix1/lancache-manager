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
 * One client row: identity + last activity, hit rate readout, and an expanded
 * detail strip of downloads/data/hits/misses. Replaces the old ClientRow/ClientCard
 * pair with a single responsive item (CSS reflows it, no forked component tree).
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
    <div>
      <div className="mgmt-row mgmt-row--interactive">
        {client.isGrouped && <Users className="w-4 h-4 text-themed-muted flex-shrink-0" />}
        <div className="mgmt-row__body">
          <p className="mgmt-row__title flex items-center gap-2">
            {ipTooltip ? (
              <Tooltip content={ipTooltip}>
                <span className="cursor-help border-b border-dashed border-themed-muted">
                  {displayLabel}
                </span>
              </Tooltip>
            ) : (
              <span>{displayLabel}</span>
            )}
            {showGroupCount && (
              <span
                className="themed-badge status-badge-neutral badge-count"
                aria-label={t('clients.groupCount', { count: client.groupMemberIps!.length })}
              >
                {client.groupMemberIps!.length}
              </span>
            )}
          </p>
          <p className="mgmt-row__meta">
            {t('clients.labels.lastActivity', { time: formattedLastActivity })}
          </p>
        </div>
        <div className="mgmt-row__actions">
          <div className="flex flex-col items-end">
            <span className={`dash-readout-value ${hitRateTone}`}>
              {formatPercent(client.cacheHitPercent)}
            </span>
            <span className="dash-readout-label">{t('clients.table.hitRate')}</span>
          </div>
        </div>
      </div>
      <div className="mgmt-row-detail">
        <div className="dash-readout">
          <div className="dash-readout-item">
            <span className="dash-readout-value">{client.totalDownloads}</span>
            <span className="dash-readout-label">{t('clients.labels.downloads')}</span>
          </div>
          <div className="dash-readout-item">
            <span className="dash-readout-value">{formatBytes(client.totalBytes)}</span>
            <span className="dash-readout-label">{t('clients.labels.totalData')}</span>
          </div>
          <div className="dash-readout-item">
            <span className="dash-readout-value is-success">
              {formatBytes(client.totalCacheHitBytes)}
            </span>
            <span className="dash-readout-label">{t('clients.labels.cacheHits')}</span>
          </div>
          <div className="dash-readout-item">
            <span className="dash-readout-value is-warning">
              {formatBytes(client.totalCacheMissBytes)}
            </span>
            <span className="dash-readout-label">{t('clients.labels.cacheMisses')}</span>
          </div>
        </div>
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
          <div className="mgmt-list" aria-hidden="true">
            {Array.from({ length: 5 }, (_, i) => (
              <div key={i} className="mgmt-row">
                <div className="mgmt-row__body">
                  <div className="clients-skeleton-line clients-skeleton-line--title" />
                  <div className="clients-skeleton-line clients-skeleton-line--meta" />
                </div>
                <div className="clients-skeleton-line clients-skeleton-line--value" />
              </div>
            ))}
          </div>
        ) : sortedClients.length > 0 ? (
          <div className="mgmt-list">
            {sortedClients.map((client) => (
              <ClientListItem key={getClientKey(client)} client={client} />
            ))}
          </div>
        ) : (
          <div className="dash-well p-3">
            <EmptyState variant="panel" icon={Users} title={t('clients.empty')} />
          </div>
        )}
      </Card>
    </div>
  );
};

export default ClientsTab;
