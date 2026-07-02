import React, { memo } from 'react';
import { useTranslation } from 'react-i18next';
import { Zap } from 'lucide-react';

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
import { getBannerImageClass, type BannerImageRendering } from './bannerImageRendering';
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

// Circular Efficiency Gauge Component
const EfficiencyGauge: React.FC<{ percent: number; size?: number }> = ({ percent, size = 56 }) => {
  const { t } = useTranslation();
  const strokeWidth = 4;
  const radius = (size - strokeWidth) / 2;
  const circumference = 2 * Math.PI * radius;
  const offset = circumference - (percent / 100) * circumference;

  const getColor = () => {
    if (percent >= 90) return 'var(--theme-success)';
    if (percent >= 50) return 'var(--theme-warning)';
    return 'var(--theme-error)';
  };

  const getLabel = () => {
    if (percent >= 90) return t('downloads.tab.retro.gauge.excellent');
    if (percent >= 50) return t('downloads.tab.retro.gauge.partial');
    return t('downloads.tab.retro.gauge.miss');
  };

  return (
    <div className="flex flex-col items-center gap-0.5">
      <div className="relative" style={{ width: size, height: size }}>
        <svg width={size} height={size} className="transform -rotate-90">
          {/* Background track */}
          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            fill="none"
            stroke="var(--theme-progress-bg)"
            strokeWidth={strokeWidth}
          />
          {/* Progress arc */}
          <circle
            cx={size / 2}
            cy={size / 2}
            r={radius}
            fill="none"
            stroke={getColor()}
            strokeWidth={strokeWidth}
            strokeLinecap="round"
            strokeDasharray={circumference}
            strokeDashoffset={offset}
            className="transition-[stroke-dashoffset,stroke] duration-500 ease-out"
          />
        </svg>
        {/* Center percentage */}
        <div
          className="absolute inset-0 flex items-center justify-center font-bold text-sm tabular-nums"
          style={{ color: getColor() }}
        >
          {Math.round(percent)}%
        </div>
      </div>
      <span
        className="text-[9px] font-medium uppercase tracking-wide"
        style={{ color: getColor() }}
      >
        {getLabel()}
      </span>
    </div>
  );
};

// Combined Progress Bar Component
const CombinedProgressBar: React.FC<{
  hitBytes: number;
  missBytes: number;
  totalBytes: number;
  showLabels?: boolean;
}> = ({ hitBytes, missBytes, totalBytes, showLabels = true }) => {
  const hitPercent = totalBytes > 0 ? (hitBytes / totalBytes) * 100 : 0;
  const missPercent = totalBytes > 0 ? (missBytes / totalBytes) * 100 : 0;

  return (
    <div className="flex flex-col gap-1.5 min-w-0 w-full max-w-full overflow-hidden">
      {/* Combined bar */}
      <div className="h-2 rounded-full overflow-hidden flex w-full bg-[var(--theme-progress-bg)]">
        {/* Cache Hit portion */}
        <div
          className="h-full transition-[width] duration-500 ease-out"
          style={{
            width: `${hitPercent}%`,
            background:
              hitPercent > 0
                ? 'linear-gradient(90deg, var(--theme-chart-cache-hit), var(--theme-chart-hit-highlight))'
                : 'transparent'
          }}
        />
        {/* Cache Miss portion */}
        <div
          className="h-full transition-[width] duration-500 ease-out"
          style={{
            width: `${missPercent}%`,
            background:
              missPercent > 0
                ? 'linear-gradient(90deg, var(--theme-error), var(--theme-chart-miss-deep))'
                : 'transparent'
          }}
        />
      </div>
      {/* Labels - with truncation support for mobile */}
      {showLabels && (
        <div className="flex justify-between text-[10px] min-w-0 gap-2">
          <span className="truncate tabular-nums text-[var(--theme-chart-cache-hit)]">
            {formatBytes(hitBytes)} ({formatPercent(hitPercent)})
          </span>
          <span className="truncate text-right tabular-nums text-[var(--theme-error)]">
            {formatBytes(missBytes)} ({formatPercent(missPercent)})
          </span>
        </div>
      )}
    </div>
  );
};

interface RetroRowProps {
  data: RetroRowData;
  isDesktop: boolean;
  showTimestamps: boolean;
  showBannerColumn: boolean;
  showDatasourceColumn: boolean;
  /** Mobile-only inline datasource badge (desktop uses the dedicated column). */
  showDatasourceBadge: boolean;
  bannerImageRendering: BannerImageRendering;
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
    isDesktop,
    showTimestamps,
    showBannerColumn,
    showDatasourceColumn,
    showDatasourceBadge,
    bannerImageRendering,
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
      timeRange,
      accentColor,
      hasGameImage,
      nameKeyedService,
      nameKeyedSlug,
      onDiskSizeBytes,
      events
    } = data;

    const isVirtual = translateY !== undefined;

    return (
      <div
        data-index={dataIndex}
        ref={measureRef as React.Ref<HTMLDivElement> | undefined}
        className={isVirtual ? 'virtual-row' : undefined}
        style={isVirtual ? { transform: `translateY(${translateY}px)` } : undefined}
      >
        <div
          className={`w-full hover:bg-[var(--theme-bg-tertiary)]/50 group relative border-b border-[var(--theme-border-secondary)]${data.isEvicted ? ' opacity-60' : ''}`}
        >
          {/* Left accent border based on efficiency */}
          <div
            className="absolute left-0 top-0 bottom-0 w-1 opacity-70"
            style={{ backgroundColor: accentColor }}
          />

          {/* Conditional Layout - Mobile or Desktop based on JS breakpoint detection */}
          {isDesktop ? (
            /* Desktop Layout */
            <div className="retro-grid-row pl-4 pr-4 py-3 items-center" data-row>
              {/* Timestamp */}
              {showTimestamps && (
                <div
                  className="px-2 min-w-0 text-xs text-[var(--theme-text-secondary)] overflow-hidden whitespace-nowrap"
                  data-cell
                >
                  <span className="block truncate tabular-nums" title={timeRange}>
                    {timeRange}
                  </span>
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
                      className={`w-[120px] h-[56px] rounded object-cover ${getBannerImageClass('retro-banner-image', bannerImageRendering)}`}
                      onError={onImageError}
                    />
                  ) : (
                    /* Service icon placeholder */
                    <div className="w-[120px] h-[56px] rounded flex items-center justify-center bg-[var(--theme-bg-tertiary)]">
                      {getServiceIcon(data.service, 32)}
                    </div>
                  )}
                </div>
              )}

              {/* App name */}
              <div className="px-2 min-w-0 overflow-hidden" data-cell>
                <div className="flex flex-col min-w-0 overflow-hidden">
                  <span
                    className="text-sm font-medium text-[var(--theme-text-primary)] truncate"
                    title={data.gameName || data.service}
                  >
                    {data.gameName || data.service}
                  </span>
                  <BadgesRow
                    service={data.service}
                    showDatasource={false}
                    isEvicted={data.isEvicted}
                    isPartiallyEvicted={data.isPartiallyEvicted}
                  />
                  {onDiskSizeBytes ? (
                    <span className="text-themed-muted text-xs ml-2">
                      {t('dashboard.downloadsPanel.onDisk', {
                        size: formatBytes(onDiskSizeBytes)
                      })}
                    </span>
                  ) : null}
                  {data.requestCount > 1 && (
                    <span className="text-xs text-[var(--theme-text-muted)] truncate">
                      {t('downloads.tab.retro.clientCount', {
                        count: data.clientsSet.size
                      })}{' '}
                      ·{' '}
                      {t('downloads.tab.retro.requestCount', {
                        count: data.requestCount
                      })}
                    </span>
                  )}
                </div>
              </div>

              {/* Datasource - only shown when multiple datasources exist */}
              {showDatasourceColumn && (
                <div className="px-2 min-w-0 overflow-hidden text-center" data-cell>
                  <span
                    className="themed-badge status-badge-neutral inline-block truncate max-w-full"
                    title={data.datasource}
                  >
                    {data.datasource || t('downloads.tab.retro.notAvailable')}
                  </span>
                </div>
              )}

              {/* Events - shows event badges for associated downloads */}
              <div className="px-2 min-w-0 overflow-hidden flex justify-center" data-cell>
                {events.length > 0 ? (
                  <DownloadBadges events={events} maxVisible={2} size="sm" />
                ) : (
                  <span className="text-xs text-[var(--theme-text-muted)]">-</span>
                )}
              </div>

              {/* Depot */}
              <div className="px-2 min-w-0 overflow-hidden text-center" data-cell>
                {data.depotsSet.size > 1 ? (
                  <span
                    className="text-xs text-[var(--theme-text-muted)] truncate block"
                    title={t('downloads.tab.retro.depotCount', {
                      count: data.depotsSet.size
                    })}
                  >
                    {t('downloads.tab.retro.depotCount', {
                      count: data.depotsSet.size
                    })}
                  </span>
                ) : data.depotId ? (
                  <a
                    href={`https://steamdb.info/depot/${data.depotId}/`}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-sm font-mono text-[var(--theme-primary)] hover:underline"
                  >
                    {data.depotId}
                  </a>
                ) : (
                  <span className="text-sm text-[var(--theme-text-muted)]">
                    {t('downloads.tab.retro.notAvailable')}
                  </span>
                )}
              </div>

              {/* Client IP */}
              <div
                className="px-2 min-w-0 text-sm font-mono text-[var(--theme-text-primary)] overflow-hidden text-center"
                data-cell
              >
                {data.clientsSet.size > 1 ? (
                  <span
                    className="truncate block"
                    title={t('downloads.tab.retro.clientCount', {
                      count: data.clientsSet.size
                    })}
                  >
                    {t('downloads.tab.retro.clientCount', {
                      count: data.clientsSet.size
                    })}
                  </span>
                ) : (
                  <span className="block truncate">
                    <ClientIpDisplay clientIp={data.clientIp} className="inline" />
                  </span>
                )}
              </div>

              {/* Avg Speed */}
              <div
                className="px-2 min-w-0 text-sm text-[var(--theme-text-primary)] overflow-hidden flex items-center justify-center gap-1"
                data-cell
              >
                <Zap size={12} className="text-[var(--theme-warning)] opacity-70" />
                <span className="truncate tabular-nums">
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
            <div className="p-3 pl-4 space-y-2 sm:space-y-3 w-full max-w-full overflow-hidden">
              {/* App image and name */}
              <div className="flex items-center gap-3 w-full min-w-0">
                {hasGameImage && (data.gameAppId || data.epicAppId || nameKeyedSlug) ? (
                  <GameImage
                    gameAppId={nameKeyedSlug ? undefined : data.epicAppId || data.gameAppId!}
                    epicAppId={data.epicAppId || undefined}
                    nameKeyedService={nameKeyedService || undefined}
                    nameKeyedSlug={nameKeyedSlug || undefined}
                    alt={data.gameName || t('downloads.tab.retro.gameFallback')}
                    className={`w-[120px] h-[56px] rounded object-cover flex-shrink-0 ${getBannerImageClass('retro-banner-image', bannerImageRendering)}`}
                    onError={onImageError}
                  />
                ) : (
                  <div className="w-[120px] h-[56px] rounded flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]">
                    {getServiceIcon(data.service, 32)}
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
              <div className="flex items-center justify-between text-xs text-[var(--theme-text-secondary)] min-w-0">
                <span className="truncate mr-2 tabular-nums">{timeRange}</span>
                <span className="flex items-center gap-1 text-[var(--theme-text-primary)] flex-shrink-0 tabular-nums">
                  <Zap size={12} className="text-[var(--theme-warning)]" />
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
                  <EfficiencyGauge percent={hitPercent} size={44} />
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
