import React, { useState, useRef, useCallback, useEffect } from 'react';
import { formatBytes, formatPercent, formatDateTime, formatSpeed } from '@utils/formatters';
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
import type { Download, DownloadGroup } from '../../../types';

const API_BASE = '/api';

type SortOrder = 'latest' | 'oldest' | 'largest' | 'smallest' | 'service' | 'efficiency' | 'efficiency-low' | 'sessions' | 'alphabetical';

interface RetroViewProps {
  items: (Download | DownloadGroup)[];
  aestheticMode?: boolean;
  itemsPerPage: number | 'unlimited';
  currentPage: number;
  onTotalPagesChange: (totalPages: number, totalItems: number) => void;
  sortOrder?: SortOrder;
  showDatasourceLabels?: boolean;
  hasMultipleDatasources?: boolean;
}

// Default column widths
const DEFAULT_COLUMN_WIDTHS = {
  timestamp: 180,
  app: 280,
  depot: 80,
  client: 140,
  speed: 100,
  cacheHit: 130,
  cacheMiss: 130,
  overall: 90
};

const STORAGE_KEY = 'retro-view-column-widths';

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
  requestCount: number;
  clientsSet: Set<string>;
  datasource?: string;
  averageBytesPerSecond: number;
}

// Group items by depot ID for retro view display
const groupByDepot = (items: (Download | DownloadGroup)[], sortOrder: SortOrder = 'latest'): DepotGroupedData[] => {
  const depotGroups: Record<string, DepotGroupedData & { _weightedSpeedSum: number; _speedBytesSum: number }> = {};

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
            requestCount: 0,
            clientsSet: new Set<string>(),
            datasource: download.datasource,
            averageBytesPerSecond: 0,
            _weightedSpeedSum: 0,
            _speedBytesSum: 0
          };
        }

        const group = depotGroups[depotKey];
        group.cacheHitBytes += download.cacheHitBytes || 0;
        group.cacheMissBytes += download.cacheMissBytes || 0;
        group.totalBytes += download.totalBytes || 0;
        group.requestCount += 1;
        group.clientsSet.add(download.clientIp);

        // Track weighted average speed
        const speed = download.averageBytesPerSecond || 0;
        const bytes = download.totalBytes || 0;
        if (speed > 0 && bytes > 0) {
          group._weightedSpeedSum += speed * bytes;
          group._speedBytesSum += bytes;
        }

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
          requestCount: 0,
          clientsSet: new Set<string>(),
          datasource: download.datasource,
          averageBytesPerSecond: 0,
          _weightedSpeedSum: 0,
          _speedBytesSum: 0
        };
      }

      const group = depotGroups[depotKey];
      group.cacheHitBytes += download.cacheHitBytes || 0;
      group.cacheMissBytes += download.cacheMissBytes || 0;
      group.totalBytes += download.totalBytes || 0;
      group.requestCount += 1;
      group.clientsSet.add(download.clientIp);

      // Track weighted average speed
      const speed = download.averageBytesPerSecond || 0;
      const bytes = download.totalBytes || 0;
      if (speed > 0 && bytes > 0) {
        group._weightedSpeedSum += speed * bytes;
        group._speedBytesSum += bytes;
      }

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

  // Calculate final average speed for each group and remove internal tracking fields
  const grouped = Object.values(depotGroups).map((group) => {
    const { _weightedSpeedSum, _speedBytesSum, ...cleanGroup } = group;
    cleanGroup.averageBytesPerSecond = _speedBytesSum > 0 ? _weightedSpeedSum / _speedBytesSum : 0;
    return cleanGroup as DepotGroupedData;
  });

  return grouped.sort((a, b) => {
    switch (sortOrder) {
      case 'oldest':
        return new Date(a.startTimeUtc).getTime() - new Date(b.startTimeUtc).getTime();
      case 'largest':
        return b.totalBytes - a.totalBytes;
      case 'smallest':
        return a.totalBytes - b.totalBytes;
      case 'service': {
        const serviceCompare = a.service.localeCompare(b.service);
        if (serviceCompare !== 0) return serviceCompare;
        // Secondary sort by time within same service
        return new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime();
      }
      case 'efficiency': {
        const aEff = a.totalBytes > 0 ? (a.cacheHitBytes / a.totalBytes) * 100 : 0;
        const bEff = b.totalBytes > 0 ? (b.cacheHitBytes / b.totalBytes) * 100 : 0;
        return bEff - aEff;
      }
      case 'efficiency-low': {
        const aEffLow = a.totalBytes > 0 ? (a.cacheHitBytes / a.totalBytes) * 100 : 0;
        const bEffLow = b.totalBytes > 0 ? (b.cacheHitBytes / b.totalBytes) * 100 : 0;
        return aEffLow - bEffLow;
      }
      case 'sessions':
        return b.requestCount - a.requestCount;
      case 'alphabetical':
        return a.gameName.localeCompare(b.gameName);
      case 'latest':
      default:
        return new Date(b.endTimeUtc).getTime() - new Date(a.endTimeUtc).getTime();
    }
  });
};

// Column resize handle component
const ResizeHandle: React.FC<{
  onMouseDown: (e: React.MouseEvent) => void;
}> = ({ onMouseDown }) => (
  <div
    className="absolute right-0 top-0 bottom-0 w-1 cursor-col-resize group z-10"
    onMouseDown={onMouseDown}
  >
    <div
      className="absolute right-0 top-1 bottom-1 w-0.5 rounded opacity-0 group-hover:opacity-100 transition-opacity"
      style={{ backgroundColor: 'var(--theme-primary)' }}
    />
  </div>
);

const RetroView: React.FC<RetroViewProps> = ({
  items,
  aestheticMode = false,
  itemsPerPage,
  currentPage,
  onTotalPagesChange,
  sortOrder = 'latest',
  showDatasourceLabels = true,
  hasMultipleDatasources = false
}) => {
  const [imageErrors, setImageErrors] = React.useState<Set<string>>(new Set());

  // Column widths state - load from localStorage or use defaults
  const [columnWidths, setColumnWidths] = useState<typeof DEFAULT_COLUMN_WIDTHS>(() => {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        return { ...DEFAULT_COLUMN_WIDTHS, ...JSON.parse(saved) };
      }
    } catch {
      // Ignore localStorage errors
    }
    return DEFAULT_COLUMN_WIDTHS;
  });

  // Resize state
  const [resizing, setResizing] = useState<keyof typeof DEFAULT_COLUMN_WIDTHS | null>(null);
  const startXRef = useRef(0);
  const startWidthRef = useRef(0);
  const containerRef = useRef<HTMLDivElement>(null);

  // Save column widths to localStorage
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(columnWidths));
    } catch {
      // Ignore localStorage errors
    }
  }, [columnWidths]);

  const handleMouseDown = useCallback((column: keyof typeof DEFAULT_COLUMN_WIDTHS, e: React.MouseEvent) => {
    e.preventDefault();
    setResizing(column);
    startXRef.current = e.clientX;
    startWidthRef.current = columnWidths[column];
  }, [columnWidths]);

  const handleMouseMove = useCallback((e: MouseEvent) => {
    if (!resizing) return;

    const diff = e.clientX - startXRef.current;
    const newWidth = Math.max(50, startWidthRef.current + diff); // Min 50px

    setColumnWidths(prev => ({
      ...prev,
      [resizing]: newWidth
    }));
  }, [resizing]);

  const handleMouseUp = useCallback(() => {
    setResizing(null);
  }, []);

  // Add/remove mouse event listeners for resizing
  useEffect(() => {
    if (resizing) {
      document.addEventListener('mousemove', handleMouseMove);
      document.addEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
    }

    return () => {
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
  }, [resizing, handleMouseMove, handleMouseUp]);

  // Reset to default widths
  const handleResetWidths = useCallback(() => {
    setColumnWidths(DEFAULT_COLUMN_WIDTHS);
  }, []);

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
  };

  // Group items by depot ID
  const allGroupedItems = React.useMemo(() => groupByDepot(items, sortOrder), [items, sortOrder]);

  // Calculate pagination based on grouped items
  const totalPages = React.useMemo(() => {
    if (itemsPerPage === 'unlimited') return 1;
    return Math.ceil(allGroupedItems.length / itemsPerPage);
  }, [allGroupedItems.length, itemsPerPage]);

  // Notify parent of total pages and items whenever they change
  React.useEffect(() => {
    onTotalPagesChange(totalPages, allGroupedItems.length);
  }, [totalPages, allGroupedItems.length, onTotalPagesChange]);

  // Apply pagination to grouped items
  const groupedItems = React.useMemo(() => {
    if (itemsPerPage === 'unlimited') {
      return allGroupedItems;
    }
    const startIndex = (currentPage - 1) * itemsPerPage;
    const endIndex = startIndex + itemsPerPage;
    return allGroupedItems.slice(startIndex, endIndex);
  }, [allGroupedItems, currentPage, itemsPerPage]);

  // Generate grid template from column widths
  const gridTemplate = `${columnWidths.timestamp}px ${columnWidths.app}px ${columnWidths.depot}px ${columnWidths.client}px ${columnWidths.speed}px ${columnWidths.cacheHit}px ${columnWidths.cacheMiss}px ${columnWidths.overall}px`;

  return (
    <div ref={containerRef} className="rounded-lg border overflow-hidden retro-table-container" style={{ borderColor: 'var(--theme-border-primary)' }}>
      {/* Desktop Table Header - hidden on mobile */}
      <div
        className="hidden lg:grid gap-2 px-3 py-3 text-xs font-semibold uppercase tracking-wide border-b select-none"
        style={{
          gridTemplateColumns: gridTemplate,
          backgroundColor: 'var(--theme-bg-tertiary)',
          borderColor: 'var(--theme-border-secondary)',
          color: 'var(--theme-text-secondary)'
        }}
      >
        <div className="relative pr-2">
          Timestamp
          <ResizeHandle onMouseDown={(e) => handleMouseDown('timestamp', e)} />
        </div>
        <div className="relative pr-2">
          App
          <ResizeHandle onMouseDown={(e) => handleMouseDown('app', e)} />
        </div>
        <div className="relative pr-2">
          Depot
          <ResizeHandle onMouseDown={(e) => handleMouseDown('depot', e)} />
        </div>
        <div className="relative pr-2">
          Client
          <ResizeHandle onMouseDown={(e) => handleMouseDown('client', e)} />
        </div>
        <div className="relative pr-2">
          Avg Speed
          <ResizeHandle onMouseDown={(e) => handleMouseDown('speed', e)} />
        </div>
        <div className="relative pr-2">
          Cache Hit
          <ResizeHandle onMouseDown={(e) => handleMouseDown('cacheHit', e)} />
        </div>
        <div className="relative pr-2">
          Cache Miss
          <ResizeHandle onMouseDown={(e) => handleMouseDown('cacheMiss', e)} />
        </div>
        <div className="text-center flex items-center justify-between">
          <span>Overall</span>
          <Tooltip content="Reset column widths to default">
            <button
              onClick={handleResetWidths}
              className="ml-2 p-1 rounded text-themed-muted hover:text-themed-primary transition-colors"
              style={{ fontSize: '10px' }}
            >
              ↺
            </button>
          </Tooltip>
        </div>
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
              className="transition-all duration-150 hover:bg-[var(--theme-bg-tertiary)]/50"
              style={{
                backgroundColor: 'color-mix(in srgb, var(--theme-bg-secondary) 40%, transparent)',
                borderBottom: index < items.length - 1 ? '1px solid var(--theme-border-secondary)' : 'none'
              }}
            >
              {/* Mobile Layout */}
              <div className="lg:hidden p-3 space-y-2 sm:space-y-3">
                {/* App image and name */}
                <div className="flex items-center gap-2 sm:gap-3">
                  {hasGameImage && data.gameAppId ? (
                    <img
                      src={`${API_BASE}/game-images/${data.gameAppId}/header/`}
                      alt={data.gameName || 'Game'}
                      className="w-[100px] h-[40px] sm:w-[130px] sm:h-[50px] rounded object-cover flex-shrink-0"
                      loading="lazy"
                      onError={() => handleImageError(String(data.gameAppId))}
                    />
                  ) : (
                    <div
                      className="w-[100px] h-[40px] sm:w-[130px] sm:h-[50px] rounded flex items-center justify-center flex-shrink-0"
                      style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                    >
                      {getServiceIcon(data.service, 24)}
                    </div>
                  )}
                  <div className="flex-1 min-w-0">
                    <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                      {data.gameName || data.service}
                      {data.requestCount > 1 && (
                        <span className="ml-2 text-xs text-[var(--theme-text-muted)]">
                          ({data.clientsSet.size} client{data.clientsSet.size !== 1 ? 's' : ''} · {data.requestCount} request{data.requestCount !== 1 ? 's' : ''})
                        </span>
                      )}
                    </div>
                    <div className="flex items-center gap-2 text-xs text-[var(--theme-text-muted)]">
                      <span>
                        <ClientIpDisplay clientIp={data.clientIp} className="inline" />
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
                      </span>
                      {hasMultipleDatasources && showDatasourceLabels && data.datasource && (
                        <Tooltip content={`Datasource: ${data.datasource}`}>
                          <span
                            className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0"
                            style={{
                              backgroundColor: 'var(--theme-bg-tertiary)',
                              color: 'var(--theme-text-secondary)',
                              border: '1px solid var(--theme-border-secondary)'
                            }}
                          >
                            {data.datasource}
                          </span>
                        </Tooltip>
                      )}
                    </div>
                  </div>
                </div>

                {/* Timestamp and Speed */}
                <div className="flex items-center justify-between text-xs text-[var(--theme-text-secondary)]">
                  <span>{timeRange}</span>
                  <span className="text-[var(--theme-text-primary)]">
                    Avg: {formatSpeed(data.averageBytesPerSecond)}
                  </span>
                </div>

                {/* Cache bars and Overall */}
                <div className="grid grid-cols-3 gap-1.5 sm:gap-3">
                  {/* Cache Hit */}
                  <div className="flex flex-col gap-1">
                    <div className="text-xs text-[var(--theme-text-muted)]">Hit</div>
                    <div
                      className="h-1.5 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-progress-bg)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all duration-300"
                        style={{
                          width: `${hitPercent}%`,
                          backgroundColor: hitPercent > 0 ? 'var(--theme-chart-cache-hit)' : 'transparent'
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
                      className="h-1.5 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-progress-bg)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all duration-300"
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
                          ? 'var(--theme-success-text)'
                          : hitPercent >= 50
                            ? 'var(--theme-warning-text)'
                            : 'var(--theme-error-text)'
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
              <div
                className="hidden lg:grid gap-2 px-3 py-3 items-center"
                style={{ gridTemplateColumns: gridTemplate }}
              >
                {/* Timestamp */}
                <div className="text-xs text-[var(--theme-text-secondary)] overflow-hidden">
                  <span className="block truncate" title={timeRange}>{timeRange}</span>
                </div>

                {/* App - with game image */}
                <div className="flex items-center gap-2 overflow-hidden">
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
                  <div className="flex flex-col min-w-0 overflow-hidden">
                    <div className="flex items-center gap-2">
                      <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate" title={data.gameName || data.service}>
                        {data.gameName || data.service}
                      </span>
                      {hasMultipleDatasources && showDatasourceLabels && data.datasource && (
                        <Tooltip content={`Datasource: ${data.datasource}`}>
                          <span
                            className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0"
                            style={{
                              backgroundColor: 'var(--theme-bg-tertiary)',
                              color: 'var(--theme-text-secondary)',
                              border: '1px solid var(--theme-border-secondary)'
                            }}
                          >
                            {data.datasource}
                          </span>
                        </Tooltip>
                      )}
                    </div>
                    {data.requestCount > 1 && (
                      <span className="text-xs text-[var(--theme-text-muted)]">
                        {data.clientsSet.size} client{data.clientsSet.size !== 1 ? 's' : ''} · {data.requestCount} request{data.requestCount !== 1 ? 's' : ''}
                      </span>
                    )}
                  </div>
                </div>

                {/* Depot */}
                <div className="overflow-hidden">
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

                {/* Client IP - no truncate, allow full display */}
                <div className="text-sm font-mono text-[var(--theme-text-primary)] overflow-hidden">
                  {data.clientsSet.size > 1 ? (
                    <span className="truncate block" title={`${data.clientsSet.size} clients`}>
                      {data.clientsSet.size} clients
                    </span>
                  ) : (
                    <ClientIpDisplay clientIp={data.clientIp} />
                  )}
                </div>

                {/* Avg Speed */}
                <div className="text-sm text-[var(--theme-text-primary)] overflow-hidden">
                  <span className="truncate block">{formatSpeed(data.averageBytesPerSecond)}</span>
                </div>

                {/* Cache Hit - Progress bar style */}
                <div className="flex flex-col gap-1.5 overflow-hidden">
                  <div className="flex items-center gap-2">
                    <div
                      className="flex-1 h-1.5 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-progress-bg)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all duration-300"
                        style={{
                          width: `${hitPercent}%`,
                          backgroundColor: hitPercent > 0 ? 'var(--theme-chart-cache-hit)' : 'transparent'
                        }}
                      />
                    </div>
                  </div>
                  <div className="text-xs text-[var(--theme-text-secondary)] truncate">
                    {formatBytes(cacheHitBytes)} • {formatPercent(hitPercent)}
                  </div>
                </div>

                {/* Cache Miss - Progress bar style */}
                <div className="flex flex-col gap-1.5 overflow-hidden">
                  <div className="flex items-center gap-2">
                    <div
                      className="flex-1 h-1.5 rounded-full overflow-hidden"
                      style={{ backgroundColor: 'var(--theme-progress-bg)' }}
                    >
                      <div
                        className="h-full rounded-full transition-all duration-300"
                        style={{
                          width: `${missPercent}%`,
                          backgroundColor: missPercent > 0 ? 'var(--theme-error)' : 'transparent'
                        }}
                      />
                    </div>
                  </div>
                  <div className="text-xs text-[var(--theme-text-secondary)] truncate">
                    {formatBytes(cacheMissBytes)} • {formatPercent(missPercent)}
                  </div>
                </div>

                {/* Overall - Visual indicator of cache efficiency */}
                <div className="flex flex-col items-center justify-center gap-1">
                  <div
                    className="text-lg font-bold"
                    style={{
                      color: hitPercent >= 90
                        ? 'var(--theme-success-text)'
                        : hitPercent >= 50
                          ? 'var(--theme-warning-text)'
                          : 'var(--theme-error-text)'
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
