import React, { useState, useRef, useCallback, useEffect, useMemo, useImperativeHandle, forwardRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useIsDesktop } from '@hooks/useMediaQuery';
import { formatBytes, formatPercent, formatDateTime, formatSpeed, isFromDifferentYear } from '@utils/formatters';
import { getDefaultColumnWidths, calculateColumnWidths, type ColumnWidths } from '@utils/textMeasurement';
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
import { HardDrive, Download, Zap } from 'lucide-react';
import { useDownloadAssociations } from '@contexts/DownloadAssociationsContext';
import DownloadBadges from './DownloadBadges';
import type { Download as DownloadType, DownloadGroup, EventSummary } from '../../../types';

type SortOrder = 'latest' | 'oldest' | 'largest' | 'smallest' | 'service' | 'efficiency' | 'efficiency-low' | 'sessions' | 'alphabetical';

/**
 * Format a time range with consistent year display
 * If either date is from a different year than now, both dates show the year
 * @param startTimeUtc - Start time
 * @param endTimeUtc - End time
 * @param forceYear - If true, always include year in both dates (for measurement)
 */
const formatTimeRange = (startTimeUtc: string, endTimeUtc: string, forceYear = false): string => {
  // Check if either date needs the year displayed
  const needsYear = forceYear ||
    isFromDifferentYear(startTimeUtc) ||
    isFromDifferentYear(endTimeUtc);

  const startTime = formatDateTime(startTimeUtc, needsYear);
  const endTime = formatDateTime(endTimeUtc, needsYear);

  return startTime === endTime ? startTime : `${startTime} - ${endTime}`;
};

interface RetroViewProps {
  items: (DownloadType | DownloadGroup)[];
  aestheticMode?: boolean;
  itemsPerPage: number | 'unlimited';
  currentPage: number;
  onTotalPagesChange: (totalPages: number, totalItems: number) => void;
  sortOrder?: SortOrder;
  showDatasourceLabels?: boolean;
  hasMultipleDatasources?: boolean;
}

const STORAGE_KEY = 'retro-view-column-widths';

const GRID_GAP = 8;
const GRID_PADDING = 32;
const GRID_FIXED_ADDITIONS = 20; // overall +20 in grid template
const RESIZE_MIN_WIDTH = 60;
const COLUMN_FIT_FLOOR = 40;

const MIN_COLUMN_WIDTHS: ColumnWidths = {
  timestamp: 80,
  banner: 130,
  app: 100,
  datasource: 70,
  events: 50,
  depot: 40,
  client: 60,
  speed: 50,
  cacheHit: 80,
  cacheMiss: 0,
  overall: 50
};

const getVisibleColumns = (showDatasource: boolean): (keyof ColumnWidths)[] => {
  return showDatasource
    ? ['timestamp', 'banner', 'app', 'datasource', 'events', 'depot', 'client', 'speed', 'cacheHit', 'overall']
    : ['timestamp', 'banner', 'app', 'events', 'depot', 'client', 'speed', 'cacheHit', 'overall'];
};

const getAvailableGridWidth = (containerWidth: number, showDatasource: boolean): number => {
  // +1 for banner column (now 10 or 9 columns)
  const columnCount = showDatasource ? 10 : 9;
  const gapCount = columnCount - 1;
  return containerWidth - GRID_PADDING - (gapCount * GRID_GAP) - GRID_FIXED_ADDITIONS;
};

const fitWidthsToContainer = (
  widths: ColumnWidths,
  containerWidth: number,
  showDatasource: boolean,
  lockedMinWidths?: Partial<ColumnWidths>
): ColumnWidths => {
  const availableWidth = getAvailableGridWidth(containerWidth, showDatasource);
  if (!Number.isFinite(availableWidth) || availableWidth <= 0) {
    return widths;
  }

  const columns = getVisibleColumns(showDatasource);
  const minWidths: ColumnWidths = { ...MIN_COLUMN_WIDTHS };
  if (lockedMinWidths) {
    Object.entries(lockedMinWidths).forEach(([column, value]) => {
      const key = column as keyof ColumnWidths;
      minWidths[key] = Math.max(minWidths[key], value ?? 0);
    });
  }

  const normalized: ColumnWidths = { ...widths, cacheMiss: 0 };
  columns.forEach((column) => {
    normalized[column] = Math.max(minWidths[column], normalized[column]);
  });

  const totalWidth = columns.reduce((sum, column) => sum + normalized[column], 0);
  if (totalWidth <= availableWidth) {
    return normalized;
  }

  const totalMin = columns.reduce((sum, column) => sum + minWidths[column], 0);
  if (totalMin >= availableWidth) {
    const scale = availableWidth / totalMin;
    const scaled: ColumnWidths = { ...normalized, cacheMiss: 0 };
    columns.forEach((column) => {
      scaled[column] = Math.max(COLUMN_FIT_FLOOR, Math.floor(minWidths[column] * scale));
    });
    return scaled;
  }

  const extra = availableWidth - totalMin;
  const flexTotal = columns.reduce(
    (sum, column) => sum + Math.max(0, normalized[column] - minWidths[column]),
    0
  );
  const fitted: ColumnWidths = { ...normalized, cacheMiss: 0 };
  columns.forEach((column) => {
    const flex = Math.max(0, normalized[column] - minWidths[column]);
    const share = flexTotal > 0 ? (flex / flexTotal) * extra : 0;
    fitted[column] = Math.floor(minWidths[column] + share);
  });

  return fitted;
};

const getServiceIcon = (service: string, size: number = 24) => {
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
      return <UnknownServiceIcon size={size} className="opacity-80 text-[var(--theme-text-secondary)]" />;
  }
};

// Helper to check if item is a DownloadGroup
const isDownloadGroup = (item: DownloadType | DownloadGroup): item is DownloadGroup => {
  return 'downloads' in item;
};

// Interface for grouped depot data
interface DepotGroupedData {
  id: string;
  service: string;
  gameName: string;
  gameAppId: string | null;
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
  downloadIds: number[]; // Track original download IDs for event associations
}

// Group items by depot ID for retro view display
const groupByDepot = (items: (DownloadType | DownloadGroup)[], sortOrder: SortOrder = 'latest'): DepotGroupedData[] => {
  const depotGroups: Record<string, DepotGroupedData & { _weightedSpeedSum: number; _speedBytesSum: number }> = {};

  items.forEach((item) => {
    if (isDownloadGroup(item)) {
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
            downloadIds: [],
            _weightedSpeedSum: 0,
            _speedBytesSum: 0
          };
        }

        const group = depotGroups[depotKey];
        group.downloadIds.push(download.id);
        group.cacheHitBytes += download.cacheHitBytes || 0;
        group.cacheMissBytes += download.cacheMissBytes || 0;
        group.totalBytes += download.totalBytes || 0;
        group.requestCount += 1;
        group.clientsSet.add(download.clientIp);

        const speed = download.averageBytesPerSecond || 0;
        const bytes = download.totalBytes || 0;
        if (speed > 0 && bytes > 0) {
          group._weightedSpeedSum += speed * bytes;
          group._speedBytesSum += bytes;
        }

        if (download.startTimeUtc < group.startTimeUtc) {
          group.startTimeUtc = download.startTimeUtc;
        }
        const endTime = download.endTimeUtc || download.startTimeUtc;
        if (endTime > group.endTimeUtc) {
          group.endTimeUtc = endTime;
        }
      });
    } else {
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
          downloadIds: [],
          _weightedSpeedSum: 0,
          _speedBytesSum: 0
        };
      }

      const group = depotGroups[depotKey];
      group.downloadIds.push(download.id);
      group.cacheHitBytes += download.cacheHitBytes || 0;
      group.cacheMissBytes += download.cacheMissBytes || 0;
      group.totalBytes += download.totalBytes || 0;
      group.requestCount += 1;
      group.clientsSet.add(download.clientIp);

      const speed = download.averageBytesPerSecond || 0;
      const bytes = download.totalBytes || 0;
      if (speed > 0 && bytes > 0) {
        group._weightedSpeedSum += speed * bytes;
        group._speedBytesSum += bytes;
      }

      if (download.startTimeUtc < group.startTimeUtc) {
        group.startTimeUtc = download.startTimeUtc;
      }
      const endTime = download.endTimeUtc || download.startTimeUtc;
      if (endTime > group.endTimeUtc) {
        group.endTimeUtc = endTime;
      }
    }
  });

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
        <svg
          width={size}
          height={size}
          className="transform -rotate-90"
        >
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
            className="transition-all duration-500 ease-out"
          />
        </svg>
        {/* Center percentage */}
        <div
          className="absolute inset-0 flex items-center justify-center font-bold text-sm"
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
      <div
        className="h-2 rounded-full overflow-hidden flex w-full bg-[var(--theme-progress-bg)]"
      >
        {/* Cache Hit portion */}
        <div
          className="h-full transition-all duration-500 ease-out"
          style={{
            width: `${hitPercent}%`,
            background: hitPercent > 0
              ? 'linear-gradient(90deg, var(--theme-chart-cache-hit), color-mix(in srgb, var(--theme-chart-cache-hit) 80%, white))'
              : 'transparent',
          }}
        />
        {/* Cache Miss portion */}
        <div
          className="h-full transition-all duration-500 ease-out"
          style={{
            width: `${missPercent}%`,
            background: missPercent > 0
              ? 'linear-gradient(90deg, var(--theme-error), color-mix(in srgb, var(--theme-error) 80%, black))'
              : 'transparent',
          }}
        />
      </div>
      {/* Labels - with truncation support for mobile */}
      {showLabels && (
        <div className="flex justify-between text-[10px] min-w-0 gap-2">
          <span className="truncate text-[var(--theme-chart-cache-hit)]">
            {formatBytes(hitBytes)} ({formatPercent(hitPercent)})
          </span>
          <span className="truncate text-right text-[var(--theme-error)]">
            {formatBytes(missBytes)} ({formatPercent(missPercent)})
          </span>
        </div>
      )}
    </div>
  );
};

// Empty State Component
const EmptyState: React.FC = () => {
  const { t } = useTranslation();

  return (
    <div className="flex flex-col items-center justify-center py-16 px-4">
    <div className="relative mb-6 animate-[float_3s_ease-in-out_infinite]">
      {/* Animated icon container */}
      <div className="w-20 h-20 rounded-2xl flex items-center justify-center bg-gradient-to-br from-[var(--theme-bg-tertiary)] to-[var(--theme-bg-secondary)] shadow-[0_8px_32px_rgba(0,0,0,0.2)]">
        <HardDrive
          size={36}
          className="text-[var(--theme-text-muted)] opacity-60"
        />
      </div>
      {/* Decorative elements */}
      <div className="absolute -top-2 -right-2 w-6 h-6 rounded-full flex items-center justify-center bg-[var(--theme-bg-tertiary)] border-2 border-[var(--theme-border-secondary)]">
        <Download size={12} className="text-[var(--theme-text-muted)]" />
      </div>
    </div>
    <h3 className="text-lg font-semibold mb-2 text-[var(--theme-text-primary)]">
      {t('downloads.tab.retro.empty.title')}
    </h3>
    <p className="text-sm text-center max-w-xs text-[var(--theme-text-muted)]">
      {t('downloads.tab.retro.empty.description')}
    </p>
    {/* Decorative dots */}
    <div className="flex gap-1.5 mt-6">
      {[0, 1, 2].map((i) => (
        <div
          key={i}
          className="w-1.5 h-1.5 rounded-full bg-[var(--theme-text-muted)] opacity-30"
          style={{
            animation: `pulse 1.5s ease-in-out ${i * 0.2}s infinite`,
          }}
        />
      ))}
    </div>
    </div>
  );
};

// Column resize handle component
const ResizeHandle: React.FC<{
  onMouseDown: (e: React.MouseEvent) => void;
  onDoubleClick: (e: React.MouseEvent) => void;
}> = ({ onMouseDown, onDoubleClick }) => (
  <div
    className="absolute right-0 top-0 bottom-0 w-1.5 cursor-col-resize group z-10 flex items-center justify-end"
    onMouseDown={onMouseDown}
    onDoubleClick={onDoubleClick}
  >
    {/* Subtle divider - always visible */}
    <div
      className="h-4 w-px rounded transition-all duration-150 group-hover:h-full group-hover:w-0.5 bg-[var(--theme-primary)] opacity-30"
    />
    {/* Brighter line on hover */}
    <div
      className="absolute h-full w-0.5 rounded opacity-0 group-hover:opacity-100 transition-opacity bg-[var(--theme-primary)]"
    />
  </div>
);

// Expose handleResetWidths to parent components
export interface RetroViewHandle {
  resetWidths: () => void;
}

const RetroView = forwardRef<RetroViewHandle, RetroViewProps>(({
  items,
  aestheticMode = false,
  itemsPerPage,
  currentPage,
  onTotalPagesChange,
  sortOrder = 'latest',
  showDatasourceLabels = true,
  hasMultipleDatasources = false
}, ref) => {
  const { t } = useTranslation();
  const [imageErrors, setImageErrors] = useState<Set<string>>(new Set());

  // Use JavaScript-based breakpoint detection for conditional rendering
  // This completely removes desktop layout from DOM on mobile, preventing width calculation conflicts
  const isDesktop = useIsDesktop();

  // Event associations for download badges
  const { fetchAssociations, getAssociations, refreshVersion } = useDownloadAssociations();

  // Calculate smart default widths based on content
  const smartDefaultWidths = useMemo(() => {
    return getDefaultColumnWidths();
  }, []);

  // Column widths state - load from localStorage or use smart defaults
  const [columnWidths, setColumnWidths] = useState<ColumnWidths>(() => {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        const parsed = JSON.parse(saved);
        // Merge with smart defaults to ensure all columns have valid widths
        return { ...smartDefaultWidths, ...parsed };
      }
    } catch {
      // Ignore localStorage errors
    }
    return smartDefaultWidths;
  });

  // Recalculate widths when items change (for actual data measurement)
  useEffect(() => {
    if (items.length > 0) {
      // Extract actual data for measurement
      const timestamps: string[] = [];
      const appNames: string[] = [];
      const clientIps: string[] = [];

      const grouped = groupByDepot(items, sortOrder);
      grouped.forEach((data) => {
        // Use forceYear=true to measure with maximum width (always includes year)
        const timeRange = formatTimeRange(data.startTimeUtc, data.endTimeUtc, true);
        timestamps.push(timeRange);
        appNames.push(data.gameName || data.service);
        clientIps.push(data.clientIp);
      });

      const calculatedWidths = calculateColumnWidths({ timestamps, appNames, clientIps });

      // Only update if no saved preferences exist
      const saved = localStorage.getItem(STORAGE_KEY);
      if (!saved) {
        setColumnWidths((prev) => ({
          ...prev,
          timestamp: Math.max(prev.timestamp, calculatedWidths.timestamp),
          app: Math.max(prev.app, calculatedWidths.app),
          client: Math.max(prev.client, calculatedWidths.client),
        }));
      }
    }
  }, [items, sortOrder]);

  // Container ref for measurements
  const containerRef = useRef<HTMLDivElement>(null);

  // Save column widths to localStorage
  useEffect(() => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(columnWidths));
    } catch {
      // Ignore localStorage errors
    }
  }, [columnWidths]);

  // Drag handling - using document-level events for smooth dragging
  // Reference: https://www.letsbuildui.dev/articles/resizable-tables-with-react-and-css-grid/
  const handleMouseDown = useCallback((column: keyof ColumnWidths, e: React.MouseEvent) => {
    e.preventDefault();
    e.stopPropagation();

    // Store initial values in refs
    const startX = e.clientX;
    const startWidth = columnWidths[column];

    const handleMouseMove = (moveEvent: MouseEvent) => {
      const diff = moveEvent.clientX - startX;
      const newWidth = Math.max(RESIZE_MIN_WIDTH, startWidth + diff);

      setColumnWidths(prev => ({
        ...prev,
        [column]: newWidth
      }));
    };

    const handleMouseUp = () => {
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };

    document.addEventListener('mousemove', handleMouseMove);
    document.addEventListener('mouseup', handleMouseUp);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }, [columnWidths]);

  const handleResetWidths = useCallback(() => {
    localStorage.removeItem(STORAGE_KEY);

    if (!containerRef.current) {
      setColumnWidths(smartDefaultWidths);
      return;
    }

    // Measure actual content widths for each column
    const measureSpan = document.createElement('span');
    measureSpan.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;';
    document.body.appendChild(measureSpan);

    const grouped = groupByDepot(items, sortOrder);
    const isDatasourceShown = hasMultipleDatasources && showDatasourceLabels;

    // Measure each column's required width based on actual data
    const measuredWidths: ColumnWidths = {
      timestamp: 80,
      banner: 140,
      app: 100,
      datasource: 75,
      events: 90,
      depot: 50,
      client: 70,
      speed: 60,
      cacheHit: 150,
      cacheMiss: 0,
      overall: 80
    };

    // Timestamp column - use forceYear=true to measure maximum width
    measureSpan.style.font = '400 12px system-ui, -apple-system, sans-serif';
    grouped.forEach((data) => {
      const timeRange = formatTimeRange(data.startTimeUtc, data.endTimeUtc, true);
      measureSpan.textContent = timeRange;
      measuredWidths.timestamp = Math.max(measuredWidths.timestamp, measureSpan.offsetWidth + 12);
    });

    // Banner column - fixed size for game images
    measuredWidths.banner = 140;

    // App column - text only (image is in banner column)
    measureSpan.style.font = '500 14px system-ui, -apple-system, sans-serif';
    grouped.forEach((data) => {
      measureSpan.textContent = data.gameName || data.service;
      measuredWidths.app = Math.max(measuredWidths.app, measureSpan.offsetWidth + 32);
    });

    // Datasource column
    if (isDatasourceShown) {
      measureSpan.style.font = '500 12px system-ui, -apple-system, sans-serif';
      grouped.forEach((data) => {
        measureSpan.textContent = data.datasource || t('downloads.tab.retro.notAvailable');
        measuredWidths.datasource = Math.max(measuredWidths.datasource, measureSpan.offsetWidth + 32);
      });
    }

    // Depot column
    measureSpan.style.font = '400 14px ui-monospace, monospace';
    grouped.forEach((data) => {
      measureSpan.textContent = data.depotId ? String(data.depotId) : t('downloads.tab.retro.notAvailable');
      measuredWidths.depot = Math.max(measuredWidths.depot, measureSpan.offsetWidth + 32);
    });

    // Client column
    measureSpan.style.font = '400 14px ui-monospace, monospace';
    grouped.forEach((data) => {
      if (data.clientsSet.size > 1) {
        measureSpan.textContent = t('downloads.tab.retro.clientCount', { count: data.clientsSet.size });
      } else {
        measureSpan.textContent = data.clientIp;
      }
      measuredWidths.client = Math.max(measuredWidths.client, measureSpan.offsetWidth + 32);
    });

    // Speed column
    measureSpan.style.font = '400 14px system-ui, -apple-system, sans-serif';
    grouped.forEach((data) => {
      measureSpan.textContent = formatSpeed(data.averageBytesPerSecond);
      measuredWidths.speed = Math.max(measuredWidths.speed, measureSpan.offsetWidth + 32 + 16); // icon
    });

    // Cache performance column - measure the label format
    measureSpan.style.font = '400 12px system-ui, -apple-system, sans-serif';
    measureSpan.textContent = '999.99 GB (99.9%)';
    const cacheValueWidth = measureSpan.offsetWidth;
    // Two values side by side with gap
    measuredWidths.cacheHit = Math.max(measuredWidths.cacheHit, (cacheValueWidth * 2) + 16);

    // Measure headers too
    measureSpan.style.font = '600 11px system-ui, -apple-system, sans-serif';
    measureSpan.textContent = t('downloads.tab.retro.headers.timestamp');
    measuredWidths.timestamp = Math.max(measuredWidths.timestamp, measureSpan.offsetWidth + 32);
    measureSpan.textContent = t('downloads.tab.retro.headers.banner', 'Banner');
    measuredWidths.banner = Math.max(measuredWidths.banner, measureSpan.offsetWidth + 32);
    measureSpan.textContent = t('downloads.tab.retro.headers.app');
    measuredWidths.app = Math.max(measuredWidths.app, measureSpan.offsetWidth + 32);
    if (isDatasourceShown) {
      measureSpan.textContent = t('downloads.tab.retro.headers.source');
      measuredWidths.datasource = Math.max(measuredWidths.datasource, measureSpan.offsetWidth + 32);
    }
    measureSpan.textContent = t('downloads.tab.retro.headers.events');
    measuredWidths.events = Math.max(measuredWidths.events, measureSpan.offsetWidth + 32);
    measureSpan.textContent = t('downloads.tab.retro.headers.depot');
    measuredWidths.depot = Math.max(measuredWidths.depot, measureSpan.offsetWidth + 32);
    measureSpan.textContent = t('downloads.tab.retro.headers.client');
    measuredWidths.client = Math.max(measuredWidths.client, measureSpan.offsetWidth + 32);
    measureSpan.textContent = t('downloads.tab.retro.headers.avgSpeed');
    measuredWidths.speed = Math.max(measuredWidths.speed, measureSpan.offsetWidth + 32);
    measureSpan.textContent = t('downloads.tab.retro.headers.cachePerformance');
    measuredWidths.cacheHit = Math.max(measuredWidths.cacheHit, measureSpan.offsetWidth + 16);
    measureSpan.textContent = t('downloads.tab.retro.headers.efficiency');
    measuredWidths.overall = Math.max(measuredWidths.overall, measureSpan.offsetWidth + 32);

    document.body.removeChild(measureSpan);

    const containerWidth = containerRef.current.clientWidth;
    const fittedWidths = fitWidthsToContainer(measuredWidths, containerWidth, isDatasourceShown);
    setColumnWidths(fittedWidths);
  }, [items, sortOrder, hasMultipleDatasources, showDatasourceLabels, smartDefaultWidths, t]);

  // Auto-fit a single column by measuring actual data content (not truncated DOM text)
  // Uses data from groupedItems to get full text values
  const handleAutoFitColumn = useCallback((column: keyof ColumnWidths) => {
    // Start with the smart default as baseline
    const defaultWidth = smartDefaultWidths[column];
    let maxWidth = defaultWidth;

    // Create a temporary span to measure text width
    const measureSpan = document.createElement('span');
    measureSpan.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;';
    measureSpan.style.letterSpacing = 'normal';
    document.body.appendChild(measureSpan);

    // Use the actual data to measure full text widths (not truncated DOM)
    const grouped = groupByDepot(items, sortOrder);

    // Set font based on column type
    switch (column) {
      case 'timestamp':
        // Use forceYear=true to measure with maximum possible width
        measureSpan.style.font = '400 12px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          const timeRange = formatTimeRange(data.startTimeUtc, data.endTimeUtc, true);
          measureSpan.textContent = timeRange;
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32); // padding
        });
        break;

      case 'banner':
        // Banner column: fixed size for game images (120px) + padding
        maxWidth = 140;
        break;

      case 'app':
        measureSpan.style.font = '500 14px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          measureSpan.textContent = data.gameName || data.service;
          // App name only (image is in banner column now)
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
        });
        break;

      case 'client':
        measureSpan.style.font = '400 14px ui-monospace, monospace';
        grouped.forEach((data) => {
          if (data.clientsSet.size > 1) {
            measureSpan.textContent = t('downloads.tab.retro.clientCount', { count: data.clientsSet.size });
          } else {
            measureSpan.textContent = data.clientIp;
          }
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
        });
        break;

      case 'depot':
        measureSpan.style.font = '400 14px ui-monospace, monospace';
        grouped.forEach((data) => {
          measureSpan.textContent = data.depotId ? String(data.depotId) : t('downloads.tab.retro.notAvailable');
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
        });
        break;

      case 'speed':
        measureSpan.style.font = '400 14px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          measureSpan.textContent = formatSpeed(data.averageBytesPerSecond);
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32 + 16); // extra for icon
        });
        break;

      case 'datasource':
        measureSpan.style.font = '500 12px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          measureSpan.textContent = data.datasource || t('downloads.tab.retro.notAvailable');
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
        });
        break;
      case 'cacheHit':
        measureSpan.style.font = '400 10px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          const totalBytes = data.totalBytes || 0;
          const hitPercent = totalBytes > 0 ? (data.cacheHitBytes / totalBytes) * 100 : 0;
          const missPercent = totalBytes > 0 ? (data.cacheMissBytes / totalBytes) * 100 : 0;
          const hitLabel = `${formatBytes(data.cacheHitBytes)} (${formatPercent(hitPercent)})`;
          const missLabel = `${formatBytes(data.cacheMissBytes)} (${formatPercent(missPercent)})`;
          measureSpan.textContent = hitLabel;
          const hitWidth = measureSpan.offsetWidth;
          measureSpan.textContent = missLabel;
          const missWidth = measureSpan.offsetWidth;
          maxWidth = Math.max(maxWidth, hitWidth + missWidth + 8 + 32);
        });
        break;

      default:
        // For other columns, use smart defaults
        break;
    }

    // Also measure header
    measureSpan.style.font = '600 11px system-ui, -apple-system, sans-serif';
    measureSpan.style.letterSpacing = '0.025em';
    const headerLabels: Record<string, string> = {
      timestamp: t('downloads.tab.retro.headers.timestamp'),
      banner: t('downloads.tab.retro.headers.banner', 'Banner'),
      app: t('downloads.tab.retro.headers.app'),
      datasource: t('downloads.tab.retro.headers.source'),
      events: t('downloads.tab.retro.headers.events'),
      depot: t('downloads.tab.retro.headers.depot'),
      client: t('downloads.tab.retro.headers.client'),
      speed: t('downloads.tab.retro.headers.avgSpeed'),
      cacheHit: t('downloads.tab.retro.headers.cachePerformance'),
      cacheMiss: t('downloads.tab.retro.headers.cachePerformance'),
      overall: t('downloads.tab.retro.headers.efficiency')
    };
    measureSpan.textContent = headerLabels[column] || '';
    maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);

    document.body.removeChild(measureSpan);

    const requiredWidth = Math.max(RESIZE_MIN_WIDTH, Math.ceil(maxWidth));

    setColumnWidths((prev) => {
      const nextWidths: ColumnWidths = {
        ...prev,
        [column]: requiredWidth
      };

      const containerWidth = containerRef.current?.clientWidth;
      if (!containerWidth) {
        return nextWidths;
      }

      const isDatasourceShown = hasMultipleDatasources && showDatasourceLabels;
      const lockedMinWidths = { [column]: requiredWidth } as Partial<ColumnWidths>;
      return fitWidthsToContainer(nextWidths, containerWidth, isDatasourceShown, lockedMinWidths);
    });
  }, [smartDefaultWidths, items, sortOrder, t, hasMultipleDatasources, showDatasourceLabels]);

  // Expose resetWidths to parent via ref
  useImperativeHandle(ref, () => ({
    resetWidths: handleResetWidths
  }), [handleResetWidths]);

  const handleImageError = (gameAppId: string) => {
    setImageErrors((prev) => new Set(prev).add(gameAppId));
  };

  // Group items by depot ID
  const allGroupedItems = useMemo(() => groupByDepot(items, sortOrder), [items, sortOrder]);

  // Calculate pagination based on grouped items
  const totalPages = useMemo(() => {
    if (itemsPerPage === 'unlimited') return 1;
    return Math.ceil(allGroupedItems.length / itemsPerPage);
  }, [allGroupedItems.length, itemsPerPage]);

  // Notify parent of total pages and items whenever they change
  useEffect(() => {
    onTotalPagesChange(totalPages, allGroupedItems.length);
  }, [totalPages, allGroupedItems.length, onTotalPagesChange]);

  // Apply pagination to grouped items
  const groupedItems = useMemo(() => {
    if (itemsPerPage === 'unlimited') {
      return allGroupedItems;
    }
    const startIndex = (currentPage - 1) * itemsPerPage;
    const endIndex = startIndex + itemsPerPage;
    return allGroupedItems.slice(startIndex, endIndex);
  }, [allGroupedItems, currentPage, itemsPerPage]);

  // Fetch event associations for visible downloads
  // refreshVersion triggers re-fetch when cache is invalidated (e.g., DownloadTagged event)
  useEffect(() => {
    const allDownloadIds = groupedItems.flatMap(group => group.downloadIds);
    if (allDownloadIds.length > 0) {
      fetchAssociations(allDownloadIds);
    }
  }, [groupedItems, fetchAssociations, refreshVersion]);

  // Pre-compute row data with events to avoid recalculating during render
  // This memoization prevents expensive event lookups on every render
  const rowsWithEvents = useMemo(() => {
    return groupedItems.map((data) => {
      // Aggregate events for this depot group
      const eventsMap = new Map<number, EventSummary>();
      data.downloadIds.forEach(id => {
        const associations = getAssociations(id);
        associations.events.forEach(event => {
          if (!eventsMap.has(event.id)) {
            eventsMap.set(event.id, event);
          }
        });
      });
      const events = Array.from(eventsMap.values());

      // Pre-calculate derived values
      const totalBytes = data.totalBytes || 0;
      const cacheHitBytes = data.cacheHitBytes || 0;
      const cacheMissBytes = data.cacheMissBytes || 0;
      const hitPercent = totalBytes > 0 ? (cacheHitBytes / totalBytes) * 100 : 0;
      const timeRange = formatTimeRange(data.startTimeUtc, data.endTimeUtc);

      // Get accent color
      let accentColor: string;
      if (hitPercent >= 90) accentColor = 'var(--theme-success)';
      else if (hitPercent >= 50) accentColor = 'var(--theme-warning)';
      else accentColor = 'var(--theme-error)';

      // Check if has game image
      const serviceLower = data.service.toLowerCase();
      const isSteam = serviceLower === 'steam';
      const hasGameImage = !aestheticMode && isSteam &&
        data.gameAppId &&
        data.gameName &&
        data.gameName !== 'Unknown Steam Game' &&
        !data.gameName.match(/^Steam App \d+$/) &&
        !imageErrors.has(String(data.gameAppId));

      return {
        ...data,
        events,
        totalBytes,
        cacheHitBytes,
        cacheMissBytes,
        hitPercent,
        timeRange,
        accentColor,
        hasGameImage,
      };
    });
  }, [groupedItems, getAssociations, aestheticMode, imageErrors]);

  // Generate grid template from column widths
  // Use pixel values for precise control during resize, with 1fr on the last column to fill remaining space
  // Only show datasource column when there are multiple datasources
  const showDatasourceColumn = hasMultipleDatasources && showDatasourceLabels;
  const gridTemplate = showDatasourceColumn
    ? `${columnWidths.timestamp}px ${columnWidths.banner}px ${columnWidths.app}px ${columnWidths.datasource}px ${columnWidths.events}px ${columnWidths.depot}px ${columnWidths.client}px ${columnWidths.speed}px ${columnWidths.cacheHit + columnWidths.cacheMiss}px minmax(${columnWidths.overall + 20}px, 1fr)`
    : `${columnWidths.timestamp}px ${columnWidths.banner}px ${columnWidths.app}px ${columnWidths.events}px ${columnWidths.depot}px ${columnWidths.client}px ${columnWidths.speed}px ${columnWidths.cacheHit + columnWidths.cacheMiss}px minmax(${columnWidths.overall + 20}px, 1fr)`;

  // Memoize grid template to prevent recalculation
  const gridTemplateMemo = useMemo(() => gridTemplate, [gridTemplate]);

  return (
      <div ref={containerRef} className="rounded-lg border border-[var(--theme-border-primary)] overflow-hidden retro-table-container bg-[var(--theme-card-bg)]">
      {/* Keyframe styles for animations - only float animation for empty state */}
      <style>{`
        @keyframes float {
          0%, 100% { transform: translateY(0); }
          50% { transform: translateY(-8px); }
        }
      `}</style>

      {/* Desktop Table Header - only rendered on desktop via JS conditional */}
      {isDesktop && (
        <div
          className="grid pl-4 pr-4 py-3 items-center text-xs leading-none font-semibold uppercase tracking-wide border-b select-none sticky top-0 z-20 bg-[var(--theme-bg-tertiary)] border-[var(--theme-border-secondary)] text-[var(--theme-text-secondary)] min-w-fit"
          style={{ gridTemplateColumns: gridTemplateMemo }}
        >
          <div className="relative px-2 flex items-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate">
              {t('downloads.tab.retro.headers.timestamp')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('timestamp', e)}
              onDoubleClick={() => handleAutoFitColumn('timestamp')}
            />
          </div>
          <div className="relative px-2 flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 truncate text-center">
              {t('downloads.tab.retro.headers.banner', 'Banner')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('banner', e)}
              onDoubleClick={() => handleAutoFitColumn('banner')}
            />
          </div>
          <div className="relative px-2 flex items-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate">
              {t('downloads.tab.retro.headers.app')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('app', e)}
              onDoubleClick={() => handleAutoFitColumn('app')}
            />
          </div>
          {showDatasourceColumn && (
            <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
              <span className="min-w-0 flex-1 truncate text-center">
                {t('downloads.tab.retro.headers.source')}
              </span>
              <ResizeHandle
                onMouseDown={(e) => handleMouseDown('datasource', e)}
                onDoubleClick={() => handleAutoFitColumn('datasource')}
              />
            </div>
          )}
          <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate text-center">
              {t('downloads.tab.retro.headers.events')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('events', e)}
              onDoubleClick={() => handleAutoFitColumn('events')}
            />
          </div>
          <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate text-center">
              {t('downloads.tab.retro.headers.depot')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('depot', e)}
              onDoubleClick={() => handleAutoFitColumn('depot')}
            />
          </div>
          <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate text-center">
              {t('downloads.tab.retro.headers.client')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('client', e)}
              onDoubleClick={() => handleAutoFitColumn('client')}
            />
          </div>
          <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate text-center">
              {t('downloads.tab.retro.headers.avgSpeed')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('speed', e)}
              onDoubleClick={() => handleAutoFitColumn('speed')}
            />
          </div>
          <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate text-center">
              {t('downloads.tab.retro.headers.cachePerformance')}
            </span>
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('cacheHit', e)}
              onDoubleClick={() => handleAutoFitColumn('cacheHit')}
            />
          </div>
          <div className="relative px-2 text-center flex items-center justify-center h-full min-w-0" data-header>
            <span className="min-w-0 flex-1 truncate text-center">
              {t('downloads.tab.retro.headers.efficiency')}
            </span>
          </div>
        </div>
      )}

      {/* Table Body */}
      <div>
        {rowsWithEvents.map((data) => {
          // All values are pre-computed in rowsWithEvents useMemo
          const { totalBytes, cacheHitBytes, cacheMissBytes, hitPercent, timeRange, accentColor, hasGameImage, events } = data;

          return (
            <div
              key={data.id}
              className="hover:bg-[var(--theme-bg-tertiary)]/50 group relative border-b border-[var(--theme-border-secondary)]"
            >
              {/* Left accent border based on efficiency */}
              <div
                className="absolute left-0 top-0 bottom-0 w-1 opacity-70"
                style={{ backgroundColor: accentColor }}
              />

              {/* Conditional Layout - Mobile or Desktop based on JS breakpoint detection */}
              {isDesktop ? (
                /* Desktop Layout */
                <div
                  className="grid pl-4 pr-4 py-3 items-center"
                  style={{ gridTemplateColumns: gridTemplateMemo }}
                  data-row
                >
                  {/* Timestamp */}
                  <div className="px-2 min-w-0 text-xs text-[var(--theme-text-secondary)] overflow-hidden whitespace-nowrap" data-cell>
                    <span className="block truncate" title={timeRange}>{timeRange}</span>
                  </div>

                  {/* Banner - dedicated column for game artwork */}
                  <div className="px-2 min-w-0 flex items-center justify-center" data-cell>
                    {hasGameImage && data.gameAppId ? (
                      <GameImage
                        gameAppId={data.gameAppId}
                        alt={data.gameName || t('downloads.tab.retro.gameFallback')}
                        className="w-[120px] h-[56px] rounded object-cover"
                        onFinalError={handleImageError}
                      />
                    ) : (
                      /* Service icon placeholder */
                      <div className="w-[120px] h-[56px] rounded flex items-center justify-center bg-[var(--theme-bg-tertiary)]">
                        {getServiceIcon(data.service, 32)}
                      </div>
                    )}
                  </div>

                  {/* App name */}
                  <div className="px-2 min-w-0 overflow-hidden" data-cell>
                    <div className="flex flex-col min-w-0 overflow-hidden">
                      <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate" title={data.gameName || data.service}>
                        {data.gameName || data.service}
                      </span>
                      {data.requestCount > 1 && (
                        <span className="text-xs text-[var(--theme-text-muted)] truncate">
                          {t('downloads.tab.retro.clientCount', { count: data.clientsSet.size })} ·{' '}
                          {t('downloads.tab.retro.requestCount', { count: data.requestCount })}
                        </span>
                      )}
                    </div>
                  </div>

                  {/* Datasource - only shown when multiple datasources exist */}
                  {showDatasourceColumn && (
                    <div className="px-2 min-w-0 overflow-hidden text-center" data-cell>
                      <span
                        className="px-1.5 py-0.5 text-xs font-medium rounded inline-block truncate max-w-full bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]"
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
                      <span className="text-xs text-[var(--theme-text-muted)]">—</span>
                    )}
                  </div>

                  {/* Depot */}
                  <div className="px-2 min-w-0 overflow-hidden text-center" data-cell>
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
                        <span className="text-sm text-[var(--theme-text-muted)]">
                          {t('downloads.tab.retro.notAvailable')}
                        </span>
                    )}
                  </div>

                  {/* Client IP */}
                  <div className="px-2 min-w-0 text-sm font-mono text-[var(--theme-text-primary)] overflow-hidden text-center" data-cell>
                    {data.clientsSet.size > 1 ? (
                      <span
                        className="truncate block"
                        title={t('downloads.tab.retro.clientCount', { count: data.clientsSet.size })}
                      >
                        {t('downloads.tab.retro.clientCount', { count: data.clientsSet.size })}
                      </span>
                    ) : (
                      <span className="block truncate">
                        <ClientIpDisplay clientIp={data.clientIp} className="inline" />
                      </span>
                    )}
                  </div>

                  {/* Avg Speed */}
                  <div className="px-2 min-w-0 text-sm text-[var(--theme-text-primary)] overflow-hidden flex items-center justify-center gap-1" data-cell>
                    <Zap size={12} className="text-[var(--theme-warning)] opacity-70" />
                    <span className="truncate">{formatSpeed(data.averageBytesPerSecond)}</span>
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
                    {hasGameImage && data.gameAppId ? (
                      <GameImage
                        gameAppId={data.gameAppId}
                        alt={data.gameName || t('downloads.tab.retro.gameFallback')}
                        className="w-[120px] h-[56px] rounded object-cover flex-shrink-0"
                        onFinalError={handleImageError}
                      />
                    ) : (
                      <div
                        className="w-[120px] h-[56px] rounded flex items-center justify-center flex-shrink-0 bg-[var(--theme-bg-tertiary)]"
                      >
                        {getServiceIcon(data.service, 32)}
                      </div>
                    )}
                    <div className="flex-1 min-w-0 overflow-hidden">
                      <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                        {data.gameName || data.service}
                        {data.requestCount > 1 && (
                          <span className="ml-2 text-xs text-[var(--theme-text-muted)]">
                            ({t('downloads.tab.retro.clientCount', { count: data.clientsSet.size })} ·{' '}
                            {t('downloads.tab.retro.requestCount', { count: data.requestCount })})
                          </span>
                        )}
                      </div>
                      <div className="flex items-center gap-2 text-xs text-[var(--theme-text-muted)] min-w-0">
                        <span className="truncate">
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
                          <Tooltip content={t('downloads.tab.retro.datasourceTooltip', { datasource: data.datasource })}>
                            <span
                              className="px-1.5 py-0.5 text-xs font-medium rounded flex-shrink-0 bg-[var(--theme-bg-tertiary)] text-[var(--theme-text-secondary)] border border-[var(--theme-border-secondary)]"
                            >
                              {data.datasource}
                            </span>
                          </Tooltip>
                        )}
                      </div>
                    </div>
                  </div>

                  {/* Timestamp and Speed */}
                  <div className="flex items-center justify-between text-xs text-[var(--theme-text-secondary)] min-w-0">
                    <span className="truncate mr-2">{timeRange}</span>
                    <span className="flex items-center gap-1 text-[var(--theme-text-primary)] flex-shrink-0">
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
          );
        })}
      </div>

      {rowsWithEvents.length === 0 && <EmptyState />}
      </div>
  );
});

RetroView.displayName = 'RetroView';

export default RetroView;
