import React from 'react';
import { useTranslation } from 'react-i18next';
import { ChevronDown, CheckCircle, AlertCircle } from 'lucide-react';
import Badge from '@components/ui/Badge';
import EvictedBadge from '@components/common/EvictedBadge';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { CollapsibleRegion } from '@components/ui/CollapsibleRegion';
import { formatBytes, formatPercent } from '@utils/formatters';
import DownloadBadges from './DownloadBadges';
import { DownloadTimestamp } from './DownloadTimestamp';
import IpSessionList from './IpSessionList';
import { cacheHitPercent } from './downloadGrouping';
import type { DownloadAssociations } from '@contexts/DownloadAssociationsContext.types';
import type { Download } from '../../../types';

interface IpDownloadGroupProps {
  /** Downloads bucketed by client IP, straight from `useGroupPagination`. */
  ipGroups: Record<string, Download[]>;
  /** Page size inside one IP, driven by the "Items/IP" dropdown in `SessionFilterBar`. */
  itemsPerPage: number;
  /** Event associations for a download id, from `useDownloadAssociations`. */
  getAssociations: (downloadId: number) => DownloadAssociations;
  /** Whether event badges are shown under each session row. */
  showEventBadges: boolean;
  /** Flips one IP's row open or closed. Owned by the caller so a collapsed card keeps it. */
  toggleIp: (ip: string) => void;
  /** Whether one IP's row is open, falling back to the session count when untouched. */
  isIpExpanded: (ip: string, count: number) => boolean;
}

/**
 * Collapsible per-client-IP session list. The card view and the drawer render the
 * same markup, so the evicted treatment reaches both from one place.
 */
const IpDownloadGroup: React.FC<IpDownloadGroupProps> = ({
  ipGroups,
  itemsPerPage,
  getAssociations,
  showEventBadges,
  toggleIp,
  isIpExpanded
}) => {
  const { t } = useTranslation();

  return (
    <div className="space-y-4">
      {Object.entries(ipGroups).map(([clientIp, clientDownloads]) => {
        const clientTotal = clientDownloads.reduce((sum, d) => sum + d.totalBytes, 0);
        const clientCacheHit = clientDownloads.reduce((sum, d) => sum + d.cacheHitBytes, 0);
        const expanded = isIpExpanded(clientIp, clientDownloads.length);

        return (
          <div
            key={clientIp}
            className="rounded-lg border border-[var(--theme-border-secondary)] overflow-hidden"
          >
            {/* Client header - clickable to collapse/expand */}
            <button
              type="button"
              onClick={() => toggleIp(clientIp)}
              className={`w-full bg-[var(--theme-bg-tertiary)] px-4 py-2 flex flex-wrap items-center justify-between gap-1 text-left ${expanded ? 'border-b border-[var(--theme-border-secondary)]' : ''}`}
            >
              <div className="flex items-center gap-2">
                <ChevronDown
                  size={14}
                  className={`text-[var(--theme-text-muted)] transition-transform duration-200 flex-shrink-0 ${expanded ? '' : '-rotate-90'}`}
                />
                <ClientIpDisplay
                  clientIp={clientIp}
                  className="font-mono text-xs font-bold text-[var(--theme-text-primary)]"
                />
                <Badge variant="neutral" className="uppercase tracking-wide">
                  {t('downloads.tab.normal.sessions.count', {
                    count: clientDownloads.length
                  })}
                </Badge>
              </div>
              <div className="flex items-center gap-3 text-xs">
                <span className="font-medium text-[var(--theme-text-secondary)]">
                  {t('downloads.tab.normal.labels.total')}{' '}
                  <span className="text-[var(--theme-text-primary)] font-bold">
                    {formatBytes(clientTotal)}
                  </span>
                </span>
                {clientCacheHit > 0 && (
                  <span className="font-medium text-[var(--theme-success-text)]">
                    {t('downloads.tab.normal.labels.saved')}{' '}
                    <span className="font-bold">{formatBytes(clientCacheHit)}</span>
                  </span>
                )}
              </div>
            </button>

            {/* Sessions table-like list - shown only when expanded */}
            <CollapsibleRegion open={expanded}>
              <IpSessionList
                ip={clientIp}
                items={clientDownloads}
                itemsPerPage={itemsPerPage}
                className="divide-y divide-[var(--theme-border-secondary)]"
                renderItem={(download) => {
                  const totalBytes = download.totalBytes;
                  const cachePercent = cacheHitPercent(download.cacheHitBytes, totalBytes);
                  const associations = getAssociations(download.id);

                  return (
                    <div
                      key={download.id}
                      className={`drawer-session-row px-4 py-3 transition-colors${download.isEvicted ? ' opacity-60' : ''}`}
                    >
                      <div className="sm:hidden">
                        <div className="flex items-center justify-between gap-3">
                          <div className="flex min-w-0 items-center gap-2">
                            {download.endTimeUtc ? (
                              <CheckCircle
                                size={14}
                                className="flex-shrink-0 text-[var(--theme-success-text)]"
                              />
                            ) : (
                              <AlertCircle
                                size={14}
                                className="flex-shrink-0 text-[var(--theme-info-text)]"
                              />
                            )}
                            <span className="truncate text-sm font-medium text-[var(--theme-text-primary)]">
                              <DownloadTimestamp dateString={download.startTimeUtc} />
                            </span>
                          </div>
                          <div className="flex flex-shrink-0 items-center gap-1">
                            {download.depotId && (
                              <span className="text-xs font-mono text-[var(--theme-text-muted)] bg-[var(--theme-bg-tertiary)] px-1.5 rounded">
                                {t('downloads.active.depotLabel', {
                                  depotId: download.depotId
                                })}
                              </span>
                            )}
                            {download.isEvicted && <EvictedBadge />}
                          </div>
                        </div>
                        <div className="mt-2 flex items-center justify-between pl-[22px] text-sm">
                          <span className="font-medium text-[var(--theme-text-primary)]">
                            <span className="sr-only">{t('downloads.tab.normal.size')}: </span>
                            {formatBytes(totalBytes)}
                          </span>
                          <span
                            className={
                              download.cacheHitBytes > 0
                                ? 'font-bold text-[var(--theme-success-text)]'
                                : 'text-[var(--theme-text-muted)]'
                            }
                          >
                            <span className="sr-only">{t('downloads.tab.normal.cache')}: </span>
                            {download.cacheHitBytes > 0 ? formatPercent(cachePercent) : '—'}
                          </span>
                        </div>
                        {showEventBadges && associations.events.length > 0 && (
                          <div className="mt-2 pl-[22px]">
                            <DownloadBadges events={associations.events} maxVisible={2} size="sm" />
                          </div>
                        )}
                      </div>

                      <div className="hidden items-center justify-between gap-3 sm:flex">
                        {/* Time and events */}
                        <div className="min-w-0 flex-1">
                          <div className="mb-1 flex items-center gap-2">
                            {download.endTimeUtc ? (
                              <CheckCircle size={14} className="text-[var(--theme-success-text)]" />
                            ) : (
                              <AlertCircle size={14} className="text-[var(--theme-info-text)]" />
                            )}
                            <span className="text-sm text-[var(--theme-text-primary)]">
                              <DownloadTimestamp
                                dateString={download.startTimeUtc}
                                showAbsoluteInline
                              />
                            </span>
                            {download.depotId && (
                              <span className="rounded bg-[var(--theme-bg-tertiary)] px-1.5 font-mono text-xs text-[var(--theme-text-muted)]">
                                {t('downloads.active.depotLabel', {
                                  depotId: download.depotId
                                })}
                              </span>
                            )}
                            {download.isEvicted && <EvictedBadge />}
                          </div>
                          {showEventBadges && associations.events.length > 0 && (
                            <div className="mt-1">
                              <DownloadBadges
                                events={associations.events}
                                maxVisible={3}
                                size="sm"
                              />
                            </div>
                          )}
                        </div>

                        {/* Stats */}
                        <div className="flex items-center gap-4 sm:gap-6 text-sm">
                          <div className="flex flex-col items-end">
                            <span className="text-[10px] uppercase text-[var(--theme-text-muted)] font-semibold">
                              {t('downloads.tab.normal.size')}
                            </span>
                            <span className="font-medium text-[var(--theme-text-primary)]">
                              {formatBytes(totalBytes)}
                            </span>
                          </div>
                          <div className="flex flex-col items-end w-20">
                            <span className="text-[10px] uppercase text-[var(--theme-text-muted)] font-semibold">
                              {t('downloads.tab.normal.cache')}
                            </span>
                            {download.cacheHitBytes > 0 ? (
                              <div className="flex items-center gap-1.5">
                                <span className="font-bold text-[var(--theme-success-text)]">
                                  {formatPercent(cachePercent)}
                                </span>
                              </div>
                            ) : (
                              <span className="text-[var(--theme-text-muted)]">—</span>
                            )}
                          </div>
                        </div>
                      </div>
                    </div>
                  );
                }}
              />
            </CollapsibleRegion>
          </div>
        );
      })}
    </div>
  );
};

export default IpDownloadGroup;
