import React, { useMemo, memo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes, formatPercent } from '@utils/formatters';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { CacheInfoTooltip, Tooltip } from '@components/ui/Tooltip';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { EmptyState } from '@components/ui/ManagerCard';
import { useTimezone } from '@contexts/useTimezone';
import { getEffectiveTimezone, formatShortDate } from '@utils/timezone';
import { Users, ArrowDown } from 'lucide-react';
import type { ClientStat } from '@/types';

interface TopClientsTableProps {
  clientStats?: ClientStat[];
  timeRange?: string;
  customStartDate?: Date | null;
  customEndDate?: Date | null;
  glassmorphism?: boolean;
  loading?: boolean;
}

type SortOption = 'total' | 'hits' | 'misses' | 'hitRate';

interface TopClientRowProps {
  client: {
    clientIp: string;
    displayName?: string;
    isGrouped: boolean;
    groupMemberIps?: string[];
    totalBytes: number;
    totalCacheHitBytes: number;
    totalCacheMissBytes: number;
    cacheHitPercent: number;
    lastActivityUtc: string;
  };
}

const TopClientRow: React.FC<TopClientRowProps> = ({ client }) => {
  const { t } = useTranslation();
  const formattedLastActivity = useFormattedDateTime(client.lastActivityUtc);
  const displayLabel = client.displayName || client.clientIp;
  const ipTooltip =
    client.isGrouped && client.groupMemberIps
      ? t('dashboard.topClients.ipsTooltip', { ips: client.groupMemberIps.join(', ') })
      : client.displayName
        ? t('dashboard.topClients.ipTooltip', { ip: client.clientIp })
        : undefined;

  return (
    <tr>
      <td className="text-themed-primary font-medium whitespace-nowrap">
        <div className="flex items-center gap-2">
          {client.isGrouped && <Users className="w-4 h-4 text-themed-muted flex-shrink-0" />}
          {ipTooltip ? (
            <Tooltip content={ipTooltip}>
              <span className="cursor-help border-b border-dashed border-themed-muted">
                {displayLabel}
              </span>
            </Tooltip>
          ) : (
            <span>{displayLabel}</span>
          )}
          {client.isGrouped && client.groupMemberIps && client.groupMemberIps.length > 1 && (
            <span className="text-xs text-themed-muted">({client.groupMemberIps.length})</span>
          )}
        </div>
      </td>
      <td className="text-right tabular-nums text-themed-secondary hidden sm:table-cell whitespace-nowrap">
        {formatBytes(client.totalBytes)}
      </td>
      <td className="text-right tabular-nums cache-hit hidden md:table-cell whitespace-nowrap">
        {formatBytes(client.totalCacheHitBytes)}
      </td>
      <td className="text-right tabular-nums cache-miss hidden md:table-cell whitespace-nowrap">
        {formatBytes(client.totalCacheMissBytes)}
      </td>
      <td className="text-right whitespace-nowrap">
        <span
          className={`text-xs font-semibold tabular-nums ${
            client.cacheHitPercent > 50 ? 'text-themed-success' : 'text-themed-warning'
          }`}
        >
          {formatPercent(client.cacheHitPercent)}
        </span>
      </td>
      <td className="text-right text-themed-muted hidden lg:table-cell whitespace-nowrap">
        {formattedLastActivity}
      </td>
    </tr>
  );
};

const TopClientsTable: React.FC<TopClientsTableProps> = memo(
  ({
    clientStats = [],
    timeRange = 'live',
    customStartDate,
    customEndDate,
    glassmorphism = false,
    loading = false
  }) => {
    const { t } = useTranslation();
    const { useLocalTimezone } = useTimezone();
    const [sortBy, setSortBy] = useState<SortOption>('total');

    const timeRangeLabel = useMemo(() => {
      if (timeRange === 'custom' && customStartDate && customEndDate) {
        const timezone = getEffectiveTimezone(useLocalTimezone);
        const start = formatShortDate(customStartDate, timezone);
        const end = formatShortDate(customEndDate, timezone);
        return `${start} - ${end}`;
      }

      const key = `dashboard.topClients.timeRanges.${timeRange}` as const;
      return t(key);
    }, [timeRange, customStartDate, customEndDate, useLocalTimezone, t]);

    const sortedClients = useMemo(() => {
      const sorted = [...clientStats];

      switch (sortBy) {
        case 'total':
          sorted.sort((a, b) => b.totalBytes - a.totalBytes);
          break;
        case 'hits':
          sorted.sort((a, b) => b.totalCacheHitBytes - a.totalCacheHitBytes);
          break;
        case 'misses':
          sorted.sort((a, b) => b.totalCacheMissBytes - a.totalCacheMissBytes);
          break;
        case 'hitRate':
          sorted.sort((a, b) => b.cacheHitPercent - a.cacheHitPercent);
          break;
      }

      return sorted;
    }, [clientStats, sortBy]);

    const displayClients = useMemo(() => sortedClients.slice(0, 10), [sortedClients]);

    return (
      <Card glassmorphism={glassmorphism}>
        <div className="flex items-center justify-between mb-4 flex-wrap gap-2">
          <h3 className="text-lg font-semibold text-themed-primary flex items-center gap-2">
            {t('dashboard.topClients.title')}
            <CacheInfoTooltip />
          </h3>
          <div className="flex items-center gap-2">
            <EnhancedDropdown
              options={[
                { value: 'total', label: t('dashboard.topClients.sort.total') },
                { value: 'hits', label: t('dashboard.topClients.sort.hits') },
                { value: 'misses', label: t('dashboard.topClients.sort.misses') },
                { value: 'hitRate', label: t('dashboard.topClients.sort.hitRate') }
              ]}
              value={sortBy}
              onChange={(value) => setSortBy(value as SortOption)}
              className="w-48"
            />
            <span className="text-xs text-themed-muted whitespace-nowrap">{timeRangeLabel}</span>
          </div>
        </div>

        {loading ? (
          <div className="dash-well">
            <div className="overflow-x-auto">
              <table className="top-clients-table">
                <thead>
                  <tr>
                    <th scope="col">{t('dashboard.topClients.columns.client')}</th>
                    <th scope="col" className="text-right hidden sm:table-cell">
                      {t('dashboard.topClients.columns.total')}
                    </th>
                    <th scope="col" className="text-right hidden md:table-cell">
                      {t('dashboard.topClients.columns.hits')}
                    </th>
                    <th scope="col" className="text-right hidden md:table-cell">
                      {t('dashboard.topClients.columns.misses')}
                    </th>
                    <th scope="col" className="text-right">
                      {t('dashboard.topClients.columns.hitRate')}
                    </th>
                    <th scope="col" className="text-right hidden lg:table-cell">
                      {t('dashboard.topClients.columns.lastSeen')}
                    </th>
                  </tr>
                </thead>
                <tbody className="text-sm">
                  {Array.from({ length: 5 }, (_, i) => (
                    <tr key={i}>
                      <td>
                        <div className="h-4 w-24 rounded bg-[var(--theme-skeleton-base,rgba(255,255,255,0.06))] animate-pulse" />
                      </td>
                      <td className="hidden sm:table-cell">
                        <div className="h-4 w-16 rounded bg-[var(--theme-skeleton-base,rgba(255,255,255,0.06))] animate-pulse ml-auto" />
                      </td>
                      <td className="hidden md:table-cell">
                        <div className="h-4 w-16 rounded bg-[var(--theme-skeleton-base,rgba(255,255,255,0.06))] animate-pulse ml-auto" />
                      </td>
                      <td className="hidden md:table-cell">
                        <div className="h-4 w-16 rounded bg-[var(--theme-skeleton-base,rgba(255,255,255,0.06))] animate-pulse ml-auto" />
                      </td>
                      <td>
                        <div className="h-4 w-12 rounded bg-[var(--theme-skeleton-base,rgba(255,255,255,0.06))] animate-pulse ml-auto" />
                      </td>
                      <td className="text-right hidden lg:table-cell">
                        <div className="h-4 w-20 rounded bg-[var(--theme-skeleton-base,rgba(255,255,255,0.06))] animate-pulse ml-auto" />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ) : displayClients.length > 0 ? (
          <div className="dash-well">
            <div className="overflow-x-auto">
              <table className="top-clients-table">
                <thead>
                  <tr>
                    <th scope="col">{t('dashboard.topClients.columns.client')}</th>
                    <th scope="col" className="text-right hidden sm:table-cell">
                      <span className="inline-flex items-center justify-end gap-1">
                        {t('dashboard.topClients.columns.total')}
                        {sortBy === 'total' && <ArrowDown className="w-3 h-3" />}
                      </span>
                    </th>
                    <th scope="col" className="text-right hidden md:table-cell">
                      <span className="inline-flex items-center justify-end gap-1">
                        {t('dashboard.topClients.columns.hits')}
                        {sortBy === 'hits' && <ArrowDown className="w-3 h-3" />}
                      </span>
                    </th>
                    <th scope="col" className="text-right hidden md:table-cell">
                      <span className="inline-flex items-center justify-end gap-1">
                        {t('dashboard.topClients.columns.misses')}
                        {sortBy === 'misses' && <ArrowDown className="w-3 h-3" />}
                      </span>
                    </th>
                    <th scope="col" className="text-right">
                      <span className="inline-flex items-center justify-end gap-1">
                        {t('dashboard.topClients.columns.hitRate')}
                        {sortBy === 'hitRate' && <ArrowDown className="w-3 h-3" />}
                      </span>
                    </th>
                    <th scope="col" className="text-right hidden lg:table-cell">
                      {t('dashboard.topClients.columns.lastSeen')}
                    </th>
                  </tr>
                </thead>
                <tbody className="text-sm">
                  {displayClients.map((client, idx) => (
                    <TopClientRow key={`${client.clientIp}-${idx}`} client={client} />
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        ) : (
          <div className="dash-well p-3">
            <EmptyState
              variant="panel"
              icon={Users}
              title={t('dashboard.topClients.noData')}
              subtitle={t('dashboard.topClients.noDataHint')}
            />
          </div>
        )}
      </Card>
    );
  },
  (prevProps, nextProps) => {
    // Only re-render if clientStats, timeRange or glassmorphism changed
    return (
      JSON.stringify(prevProps.clientStats) === JSON.stringify(nextProps.clientStats) &&
      prevProps.timeRange === nextProps.timeRange &&
      prevProps.glassmorphism === nextProps.glassmorphism &&
      prevProps.customStartDate?.getTime() === nextProps.customStartDate?.getTime() &&
      prevProps.customEndDate?.getTime() === nextProps.customEndDate?.getTime() &&
      prevProps.loading === nextProps.loading
    );
  }
);

TopClientsTable.displayName = 'TopClientsTable';

export default TopClientsTable;
