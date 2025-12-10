import React from 'react';
import { formatBytes, formatPercent, formatDateTime } from '@utils/formatters';
import { SteamIcon } from '@components/ui/SteamIcon';
import { WsusIcon } from '@components/ui/WsusIcon';
import { RiotIcon } from '@components/ui/RiotIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { EAIcon } from '@components/ui/EAIcon';
import { BlizzardIcon } from '@components/ui/BlizzardIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { UnknownServiceIcon } from '@components/ui/UnknownServiceIcon';
import type { Download, DownloadGroup } from '../../../types';

const API_BASE = '/api';

interface RetroViewProps {
  items: (Download | DownloadGroup)[];
  aestheticMode?: boolean;
}

const getServiceIcon = (service: string, size: number = 24) => {
  const serviceLower = service.toLowerCase();
  const style = { opacity: 0.8 };

  switch (serviceLower) {
    case 'steam':
      return <SteamIcon size={size} style={{ ...style, color: 'var(--theme-steam)' }} />;
    case 'wsus':
    case 'windows':
      return <WsusIcon size={size} style={{ ...style, color: 'var(--theme-wsus)' }} />;
    case 'riot':
    case 'riotgames':
      return <RiotIcon size={size} style={{ ...style, color: 'var(--theme-riot)' }} />;
    case 'epic':
    case 'epicgames':
      return <EpicIcon size={size} style={style} />;
    case 'origin':
    case 'ea':
      return <EAIcon size={size} style={{ ...style, color: 'var(--theme-origin)' }} />;
    case 'blizzard':
    case 'battle.net':
    case 'battlenet':
      return <BlizzardIcon size={size} style={{ ...style, color: 'var(--theme-blizzard)' }} />;
    case 'xbox':
    case 'xboxlive':
      return <XboxIcon size={size} style={{ ...style, color: 'var(--theme-xbox)' }} />;
    default:
      return <UnknownServiceIcon size={size} style={{ ...style, color: 'var(--theme-text-secondary)' }} />;
  }
};

// Helper to check if item is a DownloadGroup
const isDownloadGroup = (item: Download | DownloadGroup): item is DownloadGroup => {
  return 'downloads' in item;
};

// Interface for grouped depot data
interface DepotGroupedData {
  id: string;
  service: string;
  gameName: string;
  gameAppId: number | null;
  depotId: number | null;
  clientIp: string;
  startTimeUtc: string;
  endTimeUtc: string;
  cacheHitBytes: number;
  cacheMissBytes: number;
  totalBytes: number;
  sessionCount: number;
  clientCount: number;
}

// Group items by depot ID for retro view display
const groupByDepot = (items: (Download | DownloadGroup)[]): DepotGroupedData[] => {
  const depotGroups: Record<string, DepotGroupedData> = {};

  items.forEach((item) => {
    if (isDownloadGroup(item)) {
      // For DownloadGroups, extract individual downloads and group by depot
      item.downloads.forEach((download) => {
        const depotKey = download.depotId
          ? `depot-${download.depotId}-${download.clientIp}`
          : `no-depot-${download.service}-${download.clientIp}-${download.id}`;

        if (!depotGroups[depotKey]) {
          depotGroups[depotKey] = {
            id: depotKey,
            service: download.service,
            gameName: download.gameName || download.service,
            gameAppId: download.gameAppId || null,
            depotId: download.depotId || null,
            clientIp: download.clientIp,
            startTimeUtc: download.startTimeUtc,
            endTimeUtc: download.endTimeUtc || download.startTimeUtc,
            cacheHitBytes: 0,
            cacheMissBytes: 0,
            totalBytes: 0,
            sessionCount: 0,
            clientCount: 1
          };
        }

        const group = depotGroups[depotKey];
        group.cacheHitBytes += download.cacheHitBytes || 0;
        group.cacheMissBytes += download.cacheMissBytes || 0;
        group.totalBytes += download.totalBytes || 0;
        group.sessionCount += 1;

        // Update time range
        if (download.startTimeUtc < group.startTimeUtc) {
          group.startTimeUtc = download.startTimeUtc;
        }
        const endTime = download.endTimeUtc || download.startTimeUtc;
        if (endTime > group.endTimeUtc) {
          group.endTimeUtc = endTime;
        }
      });
    } else {
      // For individual Downloads
      const download = item;
      const depotKey = download.depotId
        ? `depot-${download.depotId}-${download.clientIp}`
        : `no-depot-${download.service}-${download.clientIp}-${download.id}`;

      if (!depotGroups[depotKey]) {
        depotGroups[depotKey] = {
          id: depotKey,
          service: download.service,
          gameName: download.gameName || download.service,
          gameAppId: download.gameAppId || null,
          depotId: download.depotId || null,
          clientIp: download.clientIp,
          startTimeUtc: download.startTimeUtc,
          endTimeUtc: download.endTimeUtc || download.startTimeUtc,
          cacheHitBytes: 0,
          cacheMissBytes: 0,
          totalBytes: 0,
          sessionCount: 0,
          clientCount: 1
        };
      }

      const group = depotGroups[depotKey];
      group.cacheHitBytes += download.cacheHitBytes || 0;
      group.cacheMissBytes += download.cacheMissBytes || 0;
      group.totalBytes += download.totalBytes || 0;
      group.sessionCount += 1;

      // Update time range
      if (download.startTimeUtc < group.startTimeUtc) {
        group.startTimeUtc = download.startTimeUtc;
      }
      const endTime = download.endTimeUtc || download.startTimeUtc;
      if (endTime > group.endTimeUtc) {
        group.endTimeUtc = endTime;
      }
    }
  });

  // Sort by most recent first
  return Object.values(depotGroups).sort((a, b) =>
    new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime()
  );
};

const RetroView: React.FC<RetroViewProps> = ({ items, aestheticMode = false }) => {
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
  };

  // Group items by depot ID
  const groupedItems = React.useMemo(() => groupByDepot(items), [items]);

  return (
    <div className="rounded-lg border overflow-hidden" style={{ borderColor: 'var(--theme-border-primary)' }}>
      {/* Desktop Table Header - hidden on mobile */}
      <div
        className="hidden lg:grid grid-cols-[minmax(180px,1fr)_minmax(200px,2fr)_100px_120px_minmax(130px,1fr)_minmax(130px,1fr)_100px] gap-2 px-4 py-3 text-xs font-semibold uppercase tracking-wide border-b"
        style={{
          backgroundColor: 'var(--theme-bg-tertiary)',
          borderColor: 'var(--theme-border-secondary)',
          color: 'var(--theme-text-secondary)'
        }}
      >
        <div>Timestamp</div>
        <div>App</div>
        <div>Depot</div>
        <div>Client</div>
        <div>Cache Hit</div>
        <div>Cache Miss</div>
        <div className="text-center">Overall</div>
      </div>

      {/* Table Body */}
      <div>
        {groupedItems.map((data, index) => {
          const serviceLower = data.service.toLowerCase();
          const isSteam = serviceLower === 'steam';
          const hasGameImage = !aestheticMode && isSteam &&
            data.gameAppId &&
            data.gameName &&
            data.gameName !== 'Unknown Steam Game' &&
            !data.gameName.match(/^Steam App \d+$/) &&
            !imageErrors.has(String(data.gameAppId));

          const totalBytes = data.totalBytes || 0;
          const cacheHitBytes = data.cacheHitBytes || 0;
          const cacheMissBytes = data.cacheMissBytes || 0;
          const hitPercent = totalBytes > 0 ? (cacheHitBytes / totalBytes) * 100 : 0;
          const missPercent = totalBytes > 0 ? (cacheMissBytes / totalBytes) * 100 : 0;

          // Format timestamp range
          const startTime = formatDateTime(data.startTimeUtc);
          const endTime = formatDateTime(data.endTimeUtc);
          const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;

          return (
            <div
              key={data.id}
              className="transition-colors hover:bg-[var(--theme-bg-tertiary)]/30"
              style={{
                backgroundColor: index % 2 === 0 ? 'var(--theme-bg-secondary)' : 'var(--theme-bg-primary)',
                borderBottom: index < items.length - 1 ? '1px solid var(--theme-border-secondary)' : 'none'
              }}
            >
              {/* Mobile Layout */}
              <div className="lg:hidden p-3 space-y-3">
                {/* App image and name */}
                <div className="flex items-center gap-3">
                  {hasGameImage && data.gameAppId ? (
                    <img
                      src={`${API_BASE}/game-images/${data.gameAppId}/header/`}
                      alt={data.gameName || 'Game'}
                      className="w-[100px] h-[38px] rounded object-cover flex-shrink-0"
                      loading="lazy"
                      onError={() => handleImageError(String(data.gameAppId))}
                    />
                  ) : (
                    <div
                      className="w-[100px] h-[38px] rounded flex items-center justify-center flex-shrink-0"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      {getServiceIcon(data.service, 24)}
                    </div>
                  )}
                  <div className="flex-1 min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {data.gameName || data.service}
                      {data.sessionCount > 1 && (
                        <span className="ml-2 text-xs text-[var(--theme-text-muted)]">
                          ({data.sessionCount} sessions)
                        </span>
                      )}
                    </div>
                    <div className="text-xs text-[var(--theme-text-muted)]">
                      {data.clientIp}
                      {data.depotId && (
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
                      )}
                    </div>
                  </div>
                </div>

                {/* Timestamp */}
                <div className="text-xs text-[var(--theme-text-secondary)]">
                  {timeRange}
                </div>

                {/* Cache bars and Overall */}
                <div className="grid grid-cols-3 gap-3">
                  {/* Cache Hit */}
                  <div className="flex flex-col gap-1">
                    <div className="text-xs text-[var(--theme-text-muted)]">Hit</div>
                    <div
                      className="h-3 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${hitPercent}%`,
                          backgroundColor: hitPercent > 0 ? 'var(--theme-success)' : 'transparent'
                        }}
                      />
                    </div>
                    <div className="text-xs text-[var(--theme-text-secondary)]">
                      {formatBytes(cacheHitBytes)}
                    </div>
                  </div>

                  {/* Cache Miss */}
                  <div className="flex flex-col gap-1">
                    <div className="text-xs text-[var(--theme-text-muted)]">Miss</div>
                    <div
                      className="h-3 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${missPercent}%`,
                          backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                        }}
                      />
                    </div>
                    <div className="text-xs text-[var(--theme-text-secondary)]">
                      {formatBytes(cacheMissBytes)}
                    </div>
                  </div>

                  {/* Overall */}
                  <div className="flex flex-col items-center justify-center gap-0.5">
                    <div className="text-xs text-[var(--theme-text-muted)]">Overall</div>
                    <div
                      className="text-base font-bold"
                      style={{
                        color: hitPercent >= 90
                          ? 'var(--theme-success)'
                          : hitPercent >= 50
                            ? 'var(--theme-warning)'
                            : 'var(--theme-error)'
                      }}
                    >
                      {formatPercent(hitPercent)}
                    </div>
                    <div
                      className="text-[9px] font-medium uppercase"
                      style={{
                        color: hitPercent >= 90
                          ? 'var(--theme-success-text)'
                          : hitPercent >= 50
                            ? 'var(--theme-warning-text)'
                            : 'var(--theme-error-text)'
                      }}
                    >
                      {hitPercent >= 90 ? 'Excellent' : hitPercent >= 50 ? 'Partial' : 'Miss'}
                    </div>
                  </div>
                </div>
              </div>

              {/* Desktop Layout */}
              <div className="hidden lg:grid grid-cols-[minmax(180px,1fr)_minmax(200px,2fr)_100px_120px_minmax(130px,1fr)_minmax(130px,1fr)_100px] gap-2 px-4 py-3 items-center">
                {/* Timestamp */}
                <div className="text-xs text-[var(--theme-text-secondary)]">
                  {timeRange}
                </div>

                {/* App - with game image */}
                <div className="flex items-center gap-3">
                  {hasGameImage && data.gameAppId ? (
                    <img
                      src={`${API_BASE}/game-images/${data.gameAppId}/header/`}
                      alt={data.gameName || 'Game'}
                      className="w-[120px] h-[45px] rounded object-cover flex-shrink-0"
                      loading="lazy"
                      onError={() => handleImageError(String(data.gameAppId))}
                    />
                  ) : (
                    <div
                      className="w-[120px] h-[45px] rounded flex items-center justify-center flex-shrink-0"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      {getServiceIcon(data.service, 28)}
                    </div>
                  )}
                  <div className="flex flex-col min-w-0">
                    <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {data.gameName || data.service}
                    </span>
                    {data.sessionCount > 1 && (
                      <span className="text-xs text-[var(--theme-text-muted)]">
                        {data.sessionCount} sessions
                        {data.clientCount > 1 && ` • ${data.clientCount} clients`}
                      </span>
                    )}
                  </div>
                </div>

                {/* Depot */}
                <div>
                  {data.depotId ? (
                    <a
                      href={`https://steamdb.info/depot/${data.depotId}/`}
                      target="_blank"
                      rel="noopener noreferrer"
                      className="text-sm font-mono text-[var(--theme-primary)] hover:underline"
                    >
                      {data.depotId}
                    </a>
                  ) : (
                    <span className="text-sm text-[var(--theme-text-muted)]">N/A</span>
                  )}
                </div>

                {/* Client IP */}
                <div className="text-sm font-mono text-[var(--theme-text-primary)] truncate" title={data.clientIp}>
                  {data.clientCount > 1 ? `${data.clientCount} clients` : data.clientIp}
                </div>

                {/* Cache Hit - Progress bar style */}
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <div
                      className="flex-1 h-4 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${hitPercent}%`,
                          backgroundColor: hitPercent > 0 ? 'var(--theme-success)' : 'transparent'
                        }}
                      />
                    </div>
                  </div>
                  <div className="text-xs text-[var(--theme-text-secondary)]">
                    {formatBytes(cacheHitBytes)} • {formatPercent(hitPercent)}
                  </div>
                </div>

                {/* Cache Miss - Progress bar style */}
                <div className="flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <div
                      className="flex-1 h-4 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all"
                        style={{
                          width: `${missPercent}%`,
                          backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                        }}
                      />
                    </div>
                  </div>
                  <div className="text-xs text-[var(--theme-text-secondary)]">
                    {formatBytes(cacheMissBytes)} • {formatPercent(missPercent)}
                  </div>
                </div>

                {/* Overall - Visual indicator of cache efficiency */}
                <div className="flex flex-col items-center justify-center gap-1">
                  <div
                    className="text-lg font-bold"
                    style={{
                      color: hitPercent >= 90
                        ? 'var(--theme-success)'
                        : hitPercent >= 50
                          ? 'var(--theme-warning)'
                          : 'var(--theme-error)'
                    }}
                  >
                    {formatPercent(hitPercent)}
                  </div>
                  <div
                    className="text-[10px] font-medium uppercase tracking-wide"
                    style={{
                      color: hitPercent >= 90
                        ? 'var(--theme-success-text)'
                        : hitPercent >= 50
                          ? 'var(--theme-warning-text)'
                          : 'var(--theme-error-text)'
                    }}
                  >
                    {hitPercent >= 90 ? 'Excellent' : hitPercent >= 50 ? 'Partial' : 'Miss'}
                  </div>
                </div>
              </div>
            </div>
          );
        })}
      </div>

      {groupedItems.length === 0 && (
        <div className="px-4 py-8 text-center text-[var(--theme-text-muted)]">
          No downloads to display
        </div>
      )}
    </div>
  );
};

export default RetroView;
