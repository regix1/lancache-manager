import React, { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useStats } from '@contexts/DashboardDataContext/hooks';
import { formatBytes, formatPercent } from '@utils/formatters';
import { isSeparatedMemberRow } from '@utils/clientRows';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { Card } from '@components/ui/Card';
import Badge from '@components/ui/Badge';
import { CacheInfoTooltip, Tooltip } from '@components/ui/Tooltip';
import { EnhancedDropdown, type DropdownOption } from '@components/ui/EnhancedDropdown';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { EmptyState } from '@components/ui/ManagerCard';
import { ArrowDown, ArrowUp, Users } from 'lucide-react';
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
  const hasGroupIps = !!(client.isGrouped && (client.groupMemberIps?.length ?? 0) > 0);
  const showGroupCount = !!(client.isGrouped && (client.groupMemberIps?.length ?? 0) > 1);
  const hitRateTone = client.cacheHitPercent > 50 ? 'is-success' : 'is-warning';
  // Every member row of a separately-reported nickname shows the same label, so the
  // row's own address rides alongside it and the rows stay tellable apart without hovering.
  const showMemberIp = isSeparatedMemberRow(client);

  // The dashed underline is the affordance for "this label stands in for the address", so it
  // appears only when a nickname is displayed instead of the raw IP. Grouped rows always carry
  // the group's nickname as displayName, so this one check covers them too.
  const hasNicknameLabel = !!client.displayName;
  // Every row still gets a tooltip: this cell truncates, so a long IPv6 address on a plain row
  // would otherwise have no way to be read in full.
  const identityTooltipContent = hasNicknameLabel ? (
    <div>
      <div>{displayLabel}</div>
      <div className="text-themed-muted">
        {hasGroupIps
          ? t('clients.tooltips.groupIps', { ips: client.groupMemberIps!.join(', ') })
          : t('clients.tooltips.singleIp', { ip: client.clientIp })}
      </div>
    </div>
  ) : (
    client.clientIp
  );

  return (
    <div className="clients-grid">
      <div className="clients-cell clients-cell--client">
        {client.isGrouped && <Users className="w-4 h-4 text-themed-muted flex-shrink-0" />}
        {/* min-w-0 on the trigger keeps the wrapper shrinkable so the label still ellipsizes */}
        <Tooltip content={identityTooltipContent} className="inline-flex min-w-0">
          <span
            className={
              hasNicknameLabel
                ? 'cursor-help border-b border-dashed border-themed-muted truncate'
                : 'truncate'
            }
          >
            {displayLabel}
          </span>
        </Tooltip>
        {showMemberIp && <span className="identity-subtext truncate">{client.clientIp}</span>}
        {showGroupCount && (
          <Badge
            variant="neutral"
            className="badge-count"
            ariaLabel={t('clients.groupCount', { count: client.groupMemberIps!.length })}
          >
            {client.groupMemberIps!.length}
          </Badge>
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
  const sortOptions = useMemo<DropdownOption[]>(
    () => [
      { value: 'totalData', label: t('clients.sort.totalData') },
      { value: 'downloads', label: t('clients.sort.totalDownloads') },
      { value: 'hits', label: t('clients.sort.cacheHits') },
      { value: 'misses', label: t('clients.sort.cacheMisses') },
      { value: 'hitRate', label: t('clients.sort.hitRate') },
      { value: 'lastActivity', label: t('clients.sort.lastActivity') },
      { value: 'ip', label: t('clients.sort.clientName') }
    ],
    [t]
  );

  // Both directions stay visible as one 40px control, so switching is one click and the
  // current direction reads off the toolbar without opening anything. The arrows are the
  // control itself, and each segment carries its own tooltip for the name.
  const directionOptions = useMemo(
    () => [
      {
        value: 'desc',
        label: t('clients.sort.descending'),
        icon: <ArrowDown size={14} />,
        tooltip: t('clients.sort.descending')
      },
      {
        value: 'asc',
        label: t('clients.sort.ascending'),
        icon: <ArrowUp size={14} />,
        tooltip: t('clients.sort.ascending')
      }
    ],
    [t]
  );

  const sortedClients = useMemo(() => {
    const sorted = [...clientStats];
    const multiplier = sortDirection === 'desc' ? -1 : 1;

    sorted.sort((a, b) => {
      switch (sortBy) {
        case 'ip': {
          // Sort by display name (nickname if available, otherwise IP)
          const aName = a.displayName || a.clientIp;
          const bName = b.displayName || b.clientIp;
          const byName = aName.localeCompare(bName);
          // Every member row of a separately-reported nickname carries that same nickname, so
          // without the address as a tie-break their order is whatever the previous sort left.
          return multiplier * (byName !== 0 ? byName : a.clientIp.localeCompare(b.clientIp));
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
      <Card>
        <div className="mgmt-toolbar mb-4">
          <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
            {t('clients.subtitle')}
            <CacheInfoTooltip />
          </h3>
          <div className="flex items-center gap-2 flex-wrap">
            {/* Direction ahead of the picker, so the picker keeps the row's right edge the way it
                does on Top Clients and the dashboard panels. */}
            <SegmentedControl
              options={directionOptions}
              value={sortDirection}
              onChange={(value) => setSortDirection(value as SortDirection)}
              size="md"
              showLabels={false}
            />
            <EnhancedDropdown
              options={sortOptions}
              value={sortBy}
              onChange={(value) => setSortBy(value as SortOption)}
              prefix={t('clients.sort.prefix')}
              className="clients-sort-field"
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
