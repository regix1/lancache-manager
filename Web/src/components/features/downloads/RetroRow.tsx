import React, { memo } from 'react';
import { useTranslation } from 'react-i18next';

import type { RetroRowData } from './RetroView.types';
import { formatBytes, formatPercent, formatSpeed } from '@utils/formatters';
import { Tooltip } from '@components/ui/Tooltip';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EAIcon } from '@components/ui/EAIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
import { GameImage } from '@components/common/GameImage';
import BadgesRow from './BadgesRow';
import DownloadBadges from './DownloadBadges';

const getServiceIcon = (service: string, size = 24) => {
  const serviceLower = service.toLowerCase();

  switch (serviceLower) {
    case 'steam':
      return <SteamIcon size={size} className="opacity-80 text-[var(--theme-steam)]" />;
    case 'wsus':
    case 'windows':
      return <WsusIcon size={size} className="opacity-80 text-[var(--theme-wsus)]" />;
    case 'riot':
    case 'riotgames':
      return <RiotIcon size={size} className="opacity-80 text-[var(--theme-riot)]" />;
    case 'epic':
    case 'epicgames':
      return <EpicIcon size={size} className="opacity-80 text-[var(--theme-epic)]" />;
    case 'origin':
    case 'ea':
      return <EAIcon size={size} className="opacity-80 text-[var(--theme-origin)]" />;
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return <BlizzardIcon size={size} className="opacity-80 text-[var(--theme-blizzard)]" />;
    case 'xbox':
    case 'xboxlive':
      return <XboxIcon size={size} className="opacity-80 text-[var(--theme-xbox)]" />;
    default:
      return (
        <UnknownServiceIcon size={size} className="opacity-80 text-[var(--theme-text-secondary)]" />
      );
  }
};

// Circular efficiency gauge - the retro instrument readout for hit rate.
// Colors come from the tier class so the SVG carries no inline styling.
const GAUGE_SIZE = 44;
const GAUGE_STROKE = 4;
const GAUGE_RADIUS = (GAUGE_SIZE - GAUGE_STROKE) / 2;
const GAUGE_CIRCUMFERENCE = 2 * Math.PI * GAUGE_RADIUS;

const efficiencyTier = (percent: number): 'success' | 'warning' | 'error' => {
  if (percent >= 90) return 'success';
  if (percent >= 50) return 'warning';
  return 'error';
};

// Class names must appear as literal strings (never template-built), or
// Tailwind's content scanner purges the matching @layer components rules.
const GAUGE_TIER_CLASS: Record<'success' | 'warning' | 'error', string> = {
  success: 'retro-gauge retro-gauge-success',
  warning: 'retro-gauge retro-gauge-warning',
  error: 'retro-gauge retro-gauge-error'
};

const ROW_ACCENT_CLASS: Record<'success' | 'warning' | 'error', string> = {
  success: 'retro-row retro-row-accent-success',
  warning: 'retro-row retro-row-accent-warning',
  error: 'retro-row retro-row-accent-error'
};

const EfficiencyGauge: React.FC<{ percent: number }> = ({ percent }) => {
  const { t } = useTranslation();
  const offset = GAUGE_CIRCUMFERENCE - (percent / 100) * GAUGE_CIRCUMFERENCE;
  const tier = efficiencyTier(percent);
  const label =
    tier === 'success'
      ? t('downloads.tab.retro.gauge.excellent')
      : tier === 'warning'
        ? t('downloads.tab.retro.gauge.partial')
        : t('downloads.tab.retro.gauge.miss');

  return (
    <div className={GAUGE_TIER_CLASS[tier]}>
      <div className="retro-gauge-dial">
        <svg width={GAUGE_SIZE} height={GAUGE_SIZE} className="-rotate-90">
          {/* Background track */}
          <circle
            cx={GAUGE_SIZE / 2}
            cy={GAUGE_SIZE / 2}
            r={GAUGE_RADIUS}
            fill="none"
            stroke="var(--theme-progress-bg)"
            strokeWidth={GAUGE_STROKE}
          />
          {/* Progress arc */}
          <circle
            cx={GAUGE_SIZE / 2}
            cy={GAUGE_SIZE / 2}
            r={GAUGE_RADIUS}
            fill="none"
            stroke="currentColor"
            strokeWidth={GAUGE_STROKE}
            strokeLinecap="round"
            strokeDasharray={GAUGE_CIRCUMFERENCE}
            strokeDashoffset={offset}
            className="retro-gauge-arc"
          />
        </svg>
        <div className="retro-gauge-value">{Math.round(percent)}%</div>
      </div>
      <span className="retro-gauge-label">{label}</span>
    </div>
  );
};

// Split hit/miss bar with mono byte readouts underneath.
const CombinedProgressBar: React.FC<{
  hitBytes: number;
  missBytes: number;
  totalBytes: number;
}> = ({ hitBytes, missBytes, totalBytes }) => {
  const hitPercent = totalBytes > 0 ? (hitBytes / totalBytes) * 100 : 0;
  const missPercent = totalBytes > 0 ? (missBytes / totalBytes) * 100 : 0;

  return (
    <div className="retro-cache-cell">
      <div className="retro-cache-bar">
        <div className="retro-cache-bar-hit" style={{ width: `${hitPercent}%` }} />
        <div className="retro-cache-bar-miss" style={{ width: `${missPercent}%` }} />
      </div>
      <div className="retro-cache-labels">
        <span className="retro-cache-label-hit">
          {formatBytes(hitBytes)} ({formatPercent(hitPercent)})
        </span>
        <span className="retro-cache-label-miss">
          {formatBytes(missBytes)} ({formatPercent(missPercent)})
        </span>
      </div>
    </div>
  );
};

interface RetroRowProps {
  data: RetroRowData;
  /** Position within the current page - drives the zebra tint. */
  rowIndex: number;
  isDesktop: boolean;
  showTimestamps: boolean;
  showBannerColumn: boolean;
  showDatasourceColumn: boolean;
  /** Mobile-only inline datasource badge (desktop uses the dedicated column). */
  showDatasourceBadge: boolean;
  onImageError: (gameAppId: string) => void;
  /** Virtualization attributes - present only when the list is virtualized. */
  dataIndex?: number;
  measureRef?: (el: Element | null) => void;
  translateY?: number;
}

// One retro table row. Memoized so untouched rows skip re-rendering when only
// page chrome (fades, pagination, resize commits) changes around them.
const RetroRow: React.FC<RetroRowProps> = memo(
  ({
    data,
    rowIndex,
    isDesktop,
    showTimestamps,
    showBannerColumn,
    showDatasourceColumn,
    showDatasourceBadge,
    onImageError,
    dataIndex,
    measureRef,
    translateY
  }) => {
    const { t } = useTranslation();
    const {
      totalBytes,
      cacheHitBytes,
      cacheMissBytes,
      hitPercent,
      timeLines,
      timeRangeTitle,
      hasGameImage,
      nameKeyedService,
      nameKeyedSlug,
      onDiskSizeBytes,
      events
    } = data;

    const isVirtual = translateY !== undefined;
    const tier = efficiencyTier(hitPercent);
    const rowClasses = `${ROW_ACCENT_CLASS[tier]}${rowIndex % 2 === 1 ? ' retro-row-alt' : ''}${
      data.isEvicted ? ' retro-row-evicted' : ''
    }`;

    return (
      <div
        data-index={dataIndex}
        ref={measureRef as React.Ref<HTMLDivElement> | undefined}
        className={isVirtual ? 'virtual-row' : undefined}
        style={isVirtual ? { transform: `translateY(${translateY}px)` } : undefined}
      >
        <div className={rowClasses}>
          {/* Conditional Layout - Mobile or Desktop based on JS breakpoint detection */}
          {isDesktop ? (
            /* Desktop Layout */
            <div className="retro-grid-row retro-body-row items-center" data-row>
              {/* Timestamp - stacked start / end lines, never truncated mid-range */}
              {showTimestamps && (
                <div className="px-2 min-w-0 overflow-hidden" data-cell>
                  <Tooltip content={timeRangeTitle} position="top" className="block min-w-0">
                    <div className="retro-time">
                      <span className="truncate">{timeLines[0]}</span>
                      {timeLines[1] && (
                        <span className="retro-time-end truncate">{timeLines[1]}</span>
                      )}
                    </div>
                  </Tooltip>
                </div>
              )}

              {/* Banner - dedicated column for game artwork */}
              {showBannerColumn && (
                <div className="px-2 min-w-0 flex items-center justify-center" data-cell>
                  {hasGameImage && (data.gameAppId || data.epicAppId || nameKeyedSlug) ? (
                    <GameImage
                      gameAppId={nameKeyedSlug ? undefined : data.epicAppId || data.gameAppId!}
                      epicAppId={data.epicAppId || undefined}
                      nameKeyedService={nameKeyedService || undefined}
                      nameKeyedSlug={nameKeyedSlug || undefined}
                      alt={data.gameName || t('downloads.tab.retro.gameFallback')}
                      className="retro-banner-img"
                      onError={onImageError}
                    />
                  ) : (
                    /* Service icon placeholder */
                    <div className="retro-banner-placeholder">
                      {getServiceIcon(data.service, 28)}
                    </div>
                  )}
                </div>
              )}

              {/* App name */}
              <div className="px-2 min-w-0 overflow-hidden" data-cell>
                <div className="flex flex-col gap-0.5 min-w-0 overflow-hidden">
                  <Tooltip
                    content={data.gameName || data.service}
                    position="top"
                    className="flex min-w-0"
                  >
                    <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {data.gameName || data.service}
                    </span>
                  </Tooltip>
                  <BadgesRow
                    service={data.service}
                    datasource={data.datasource}
                    showDatasource={false}
                    isEvicted={data.isEvicted}
                    isPartiallyEvicted={data.isPartiallyEvicted}
                  />
                  {onDiskSizeBytes ? (
                    <span className="text-themed-muted text-xs truncate">
                      {t('dashboard.downloadsPanel.onDisk', {
                        size: formatBytes(onDiskSizeBytes)
                      })}
                    </span>
                  ) : null}
                </div>
              </div>

              {/* Datasource - only shown when multiple datasources exist */}
              {showDatasourceColumn && (
                <div className="px-2 min-w-0 overflow-hidden text-center" data-cell>
                  <Tooltip
                    content={data.datasource || t('downloads.tab.retro.notAvailable')}
                    position="top"
                    className="block min-w-0"
                  >
                    <span className="themed-badge status-badge-neutral inline-block truncate max-w-full">
                      {data.datasource || t('downloads.tab.retro.notAvailable')}
                    </span>
                  </Tooltip>
                </div>
              )}

              {/* Events - shows event badges for associated downloads */}
              <div className="px-2 min-w-0 overflow-hidden flex justify-center" data-cell>
                {events.length > 0 ? (
                  <DownloadBadges events={events} maxVisible={2} size="sm" />
                ) : (
                  <span className="retro-sub-value">—</span>
                )}
              </div>

              {/* Depot */}
              <div className="px-2 min-w-0 overflow-hidden text-right" data-cell>
                {data.depotsSet.size > 1 ? (
                  <Tooltip
                    content={t('downloads.tab.retro.depotCount', {
                      count: data.depotsSet.size
                    })}
                    position="top"
                    className="block min-w-0"
                  >
                    <span className="retro-mono-value text-[var(--theme-text-muted)] truncate block">
                      {t('downloads.tab.retro.depotCount', {
                        count: data.depotsSet.size
                      })}
                    </span>
                  </Tooltip>
                ) : data.depotId ? (
                  <a
                    href={`https://steamdb.info/depot/${data.depotId}/`}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="retro-mono-value text-[var(--theme-primary)] hover:underline"
                  >
                    {data.depotId}
                  </a>
                ) : (
                  <span className="retro-mono-value text-[var(--theme-text-muted)]">
                    {t('downloads.tab.retro.notAvailable')}
                  </span>
                )}
              </div>

              {/* Client */}
              <div className="px-2 min-w-0 overflow-hidden text-right" data-cell>
                {data.clientsSet.size > 1 ? (
                  <Tooltip
                    content={t('downloads.tab.retro.clientCount', {
                      count: data.clientsSet.size
                    })}
                    position="top"
                    className="block min-w-0"
                  >
                    <span className="retro-mono-value text-[var(--theme-text-primary)] truncate block">
                      {t('downloads.tab.retro.clientCount', {
                        count: data.clientsSet.size
                      })}
                    </span>
                  </Tooltip>
                ) : (
                  <span className="retro-mono-value text-[var(--theme-text-primary)] block truncate">
                    <ClientIpDisplay clientIp={data.clientIp} className="inline" />
                  </span>
                )}
                {data.requestCount > 1 && (
                  <span className="retro-sub-value block truncate">
                    {t('downloads.tab.retro.requestCount', { count: data.requestCount })}
                  </span>
                )}
              </div>

              {/* Avg Speed */}
              <div className="px-2 min-w-0 overflow-hidden text-right" data-cell>
                <span className="retro-mono-value font-medium text-[var(--theme-text-primary)] block truncate">
                  {formatSpeed(data.averageBytesPerSecond)}
                </span>
              </div>

              {/* Combined Cache Performance Bar */}
              <div className="px-2 min-w-0 overflow-hidden flex justify-center" data-cell>
                <CombinedProgressBar
                  hitBytes={cacheHitBytes}
                  missBytes={cacheMissBytes}
                  totalBytes={totalBytes}
                />
              </div>

              {/* Circular Efficiency Gauge */}
              <div className="px-2 min-w-0 flex justify-center" data-cell>
                <EfficiencyGauge percent={hitPercent} />
              </div>
            </div>
          ) : (
            /* Mobile Layout - with explicit width constraints */
            <div className="retro-body-row space-y-2 w-full max-w-full overflow-hidden">
              {/* App image and name */}
              <div className="flex items-center gap-3 w-full min-w-0">
                {hasGameImage && (data.gameAppId || data.epicAppId || nameKeyedSlug) ? (
                  <GameImage
                    gameAppId={nameKeyedSlug ? undefined : data.epicAppId || data.gameAppId!}
                    epicAppId={data.epicAppId || undefined}
                    nameKeyedService={nameKeyedService || undefined}
                    nameKeyedSlug={nameKeyedSlug || undefined}
                    alt={data.gameName || t('downloads.tab.retro.gameFallback')}
                    className="retro-banner-img flex-shrink-0"
                    onError={onImageError}
                  />
                ) : (
                  <div className="retro-banner-placeholder flex-shrink-0">
                    {getServiceIcon(data.service, 28)}
                  </div>
                )}
                <div className="flex-1 min-w-0 overflow-hidden">
                  <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                    {data.gameName || data.service}
                    {onDiskSizeBytes ? (
                      <span className="text-themed-muted text-xs ml-2">
                        {t('dashboard.downloadsPanel.onDisk', {
                          size: formatBytes(onDiskSizeBytes)
                        })}
                      </span>
                    ) : null}
                    {data.requestCount > 1 && (
                      <span className="ml-2 text-xs text-[var(--theme-text-muted)]">
                        (
                        {t('downloads.tab.retro.clientCount', {
                          count: data.clientsSet.size
                        })}{' '}
                        ·{' '}
                        {t('downloads.tab.retro.requestCount', {
                          count: data.requestCount
                        })}
                        )
                      </span>
                    )}
                  </div>
                  <BadgesRow
                    service={data.service}
                    datasource={data.datasource}
                    showDatasource={false}
                    isEvicted={data.isEvicted}
                    isPartiallyEvicted={data.isPartiallyEvicted}
                  />
                  <div className="flex items-center gap-2 text-xs text-[var(--theme-text-muted)] min-w-0">
                    <span className="truncate">
                      <ClientIpDisplay clientIp={data.clientIp} className="inline" />
                      {data.depotsSet.size > 1 ? (
                        <>
                          {' • '}
                          <span className="text-[var(--theme-text-muted)]">
                            {t('downloads.tab.retro.depotCount', {
                              count: data.depotsSet.size
                            })}
                          </span>
                        </>
                      ) : data.depotId ? (
                        <>
                          {' • '}
                          <a
                            href={`https://steamdb.info/depot/${data.depotId}/`}
                            target="_blank"
                            rel="noopener noreferrer"
                            className="text-[var(--theme-primary)] hover:underline"
                          >
                            {data.depotId}
                          </a>
                        </>
                      ) : null}
                    </span>
                    {showDatasourceBadge && data.datasource && (
                      <Tooltip
                        content={t('downloads.tab.retro.datasourceTooltip', {
                          datasource: data.datasource
                        })}
                      >
                        <span className="themed-badge status-badge-neutral">{data.datasource}</span>
                      </Tooltip>
                    )}
                  </div>
                </div>
              </div>

              {/* Timestamp and Speed */}
              <div className="flex items-center justify-between text-xs min-w-0">
                <span className="retro-mono-value text-[var(--theme-text-secondary)] truncate mr-2">
                  {timeRangeTitle}
                </span>
                <span className="retro-mono-value font-medium text-[var(--theme-text-primary)] flex-shrink-0">
                  {formatSpeed(data.averageBytesPerSecond)}
                </span>
              </div>

              {/* Combined Progress Bar and Efficiency */}
              <div className="flex items-center gap-3 w-full min-w-0">
                <div className="flex-1 min-w-0 overflow-hidden">
                  <CombinedProgressBar
                    hitBytes={cacheHitBytes}
                    missBytes={cacheMissBytes}
                    totalBytes={totalBytes}
                  />
                </div>
                <div className="flex-shrink-0">
                  <EfficiencyGauge percent={hitPercent} />
                </div>
              </div>
            </div>
          )}
        </div>
      </div>
    );
  }
);

RetroRow.displayName = 'RetroRow';

export default RetroRow;
