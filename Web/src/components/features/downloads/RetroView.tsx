import React, { useState, useRef, useCallback, useEffect, useMemo, useImperativeHandle, forwardRef } from 'react';
import { useIsDesktop } from '@hooks/useMediaQuery';
import { formatBytes, formatPercent, formatDateTime, formatSpeed } from '@utils/formatters';
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
import { HardDrive, Download, Zap } from 'lucide-react';
import { useDownloadAssociations } from '@contexts/DownloadAssociationsContext';
import DownloadBadges from './DownloadBadges';
import type { Download as DownloadType, DownloadGroup, EventSummary } from '../../../types';

const API_BASE = '/api';

type SortOrder = 'latest' | 'oldest' | 'largest' | 'smallest' | 'service' | 'efficiency' | 'efficiency-low' | 'sessions' | 'alphabetical';

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
const isDownloadGroup = (item: DownloadType | DownloadGroup): item is DownloadGroup => {
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
    if (percent >= 90) return 'Excellent';
    if (percent >= 50) return 'Partial';
    return 'Miss';
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
        className="h-2 rounded-full overflow-hidden flex w-full"
        style={{ backgroundColor: 'var(--theme-progress-bg)' }}
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
          <span className="truncate" style={{ color: 'var(--theme-chart-cache-hit)' }}>
            {formatBytes(hitBytes)} ({formatPercent(hitPercent)})
          </span>
          <span className="truncate text-right" style={{ color: 'var(--theme-error)' }}>
            {formatBytes(missBytes)} ({formatPercent(missPercent)})
          </span>
        </div>
      )}
    </div>
  );
};

// Empty State Component
const EmptyState: React.FC = () => (
  <div className="flex flex-col items-center justify-center py-16 px-4">
    <div
      className="relative mb-6"
      style={{
        animation: 'float 3s ease-in-out infinite',
      }}
    >
      {/* Animated icon container */}
      <div
        className="w-20 h-20 rounded-2xl flex items-center justify-center"
        style={{
          background: 'linear-gradient(135deg, var(--theme-bg-tertiary), var(--theme-bg-secondary))',
          boxShadow: '0 8px 32px rgba(0,0,0,0.2)',
        }}
      >
        <HardDrive
          size={36}
          style={{ color: 'var(--theme-text-muted)', opacity: 0.6 }}
        />
      </div>
      {/* Decorative elements */}
      <div
        className="absolute -top-2 -right-2 w-6 h-6 rounded-full flex items-center justify-center"
        style={{
          background: 'var(--theme-bg-tertiary)',
          border: '2px solid var(--theme-border-secondary)',
        }}
      >
        <Download size={12} style={{ color: 'var(--theme-text-muted)' }} />
      </div>
    </div>
    <h3
      className="text-lg font-semibold mb-2"
      style={{ color: 'var(--theme-text-primary)' }}
    >
      No Downloads Yet
    </h3>
    <p
      className="text-sm text-center max-w-xs"
      style={{ color: 'var(--theme-text-muted)' }}
    >
      Download activity will appear here once your Lancache starts serving content.
    </p>
    {/* Decorative dots */}
    <div className="flex gap-1.5 mt-6">
      {[0, 1, 2].map((i) => (
        <div
          key={i}
          className="w-1.5 h-1.5 rounded-full"
          style={{
            backgroundColor: 'var(--theme-text-muted)',
            opacity: 0.3,
            animation: `pulse 1.5s ease-in-out ${i * 0.2}s infinite`,
          }}
        />
      ))}
    </div>
  </div>
);

// Column resize handle component
const ResizeHandle: React.FC<{
  onMouseDown: (e: React.MouseEvent) => void;
  onDoubleClick: (e: React.MouseEvent) => void;
}> = ({ onMouseDown, onDoubleClick }) => (
  <div
    className="absolute right-0 top-0 bottom-0 w-3 cursor-col-resize group z-10 flex items-center justify-center"
    onMouseDown={onMouseDown}
    onDoubleClick={onDoubleClick}
  >
    {/* Subtle divider - always visible */}
    <div
      className="h-4 w-px rounded transition-all duration-150 group-hover:h-full group-hover:w-0.5"
      style={{
        backgroundColor: 'var(--theme-primary)',
        opacity: 0.3
      }}
    />
    {/* Brighter line on hover */}
    <div
      className="absolute h-full w-0.5 rounded opacity-0 group-hover:opacity-100 transition-opacity"
      style={{ backgroundColor: 'var(--theme-primary)' }}
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
  const [imageErrors, setImageErrors] = useState<Set<string>>(new Set());

  // Use JavaScript-based breakpoint detection for conditional rendering
  // This completely removes desktop layout from DOM on mobile, preventing width calculation conflicts
  const isDesktop = useIsDesktop();

  // Event associations for download badges
  const { fetchAssociations, getAssociations } = useDownloadAssociations();

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
        const startTime = formatDateTime(data.startTimeUtc);
        const endTime = formatDateTime(data.endTimeUtc);
        const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;
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
      const newWidth = Math.max(60, startWidth + diff); // Minimum 60px

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
      app: 100,
      datasource: 50,
      events: 90,
      depot: 50,
      client: 70,
      speed: 60,
      cacheHit: 150,
      cacheMiss: 0,
      overall: 80
    };

    // Timestamp column
    measureSpan.style.font = '400 12px system-ui, -apple-system, sans-serif';
    grouped.forEach((data) => {
      const startTime = formatDateTime(data.startTimeUtc);
      const endTime = formatDateTime(data.endTimeUtc);
      const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;
      measureSpan.textContent = timeRange;
      measuredWidths.timestamp = Math.max(measuredWidths.timestamp, measureSpan.offsetWidth + 32);
    });

    // App column
    measureSpan.style.font = '500 14px system-ui, -apple-system, sans-serif';
    grouped.forEach((data) => {
      measureSpan.textContent = data.gameName || data.service;
      // Add image width (120px) + gap (8px) + padding (32px)
      measuredWidths.app = Math.max(measuredWidths.app, measureSpan.offsetWidth + 120 + 8 + 32);
    });

    // Datasource column
    if (isDatasourceShown) {
      measureSpan.style.font = '500 12px system-ui, -apple-system, sans-serif';
      grouped.forEach((data) => {
        measureSpan.textContent = data.datasource || 'N/A';
        measuredWidths.datasource = Math.max(measuredWidths.datasource, measureSpan.offsetWidth + 32);
      });
    }

    // Depot column
    measureSpan.style.font = '400 14px ui-monospace, monospace';
    grouped.forEach((data) => {
      measureSpan.textContent = data.depotId ? String(data.depotId) : 'N/A';
      measuredWidths.depot = Math.max(measuredWidths.depot, measureSpan.offsetWidth + 32);
    });

    // Client column
    measureSpan.style.font = '400 14px ui-monospace, monospace';
    grouped.forEach((data) => {
      if (data.clientsSet.size > 1) {
        measureSpan.textContent = `${data.clientsSet.size} clients`;
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
    measuredWidths.cacheHit = Math.max(measuredWidths.cacheHit, (cacheValueWidth * 2) + 32);

    // Measure headers too
    measureSpan.style.font = '600 11px system-ui, -apple-system, sans-serif';
    measureSpan.textContent = 'TIMESTAMP';
    measuredWidths.timestamp = Math.max(measuredWidths.timestamp, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'APP';
    measuredWidths.app = Math.max(measuredWidths.app, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'SOURCE';
    measuredWidths.datasource = Math.max(measuredWidths.datasource, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'EVENTS';
    measuredWidths.events = Math.max(measuredWidths.events, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'DEPOT';
    measuredWidths.depot = Math.max(measuredWidths.depot, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'CLIENT';
    measuredWidths.client = Math.max(measuredWidths.client, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'AVG SPEED';
    measuredWidths.speed = Math.max(measuredWidths.speed, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'CACHE PERFORMANCE';
    measuredWidths.cacheHit = Math.max(measuredWidths.cacheHit, measureSpan.offsetWidth + 32);
    measureSpan.textContent = 'EFFICIENCY';
    measuredWidths.overall = Math.max(measuredWidths.overall, measureSpan.offsetWidth + 32);

    document.body.removeChild(measureSpan);

    // Calculate total measured width and available space
    const containerWidth = containerRef.current.clientWidth;
    const GRID_GAP = 8;
    const PADDING = 32;
    const NUM_COLUMNS = isDatasourceShown ? 9 : 8;
    const NUM_GAPS = NUM_COLUMNS - 1;
    const FIXED_ADDITIONS = 40 + 20; // app+40, overall+20 in grid template

    const availableWidth = containerWidth - PADDING - (NUM_GAPS * GRID_GAP) - FIXED_ADDITIONS;

    // Sum up all measured widths (excluding cacheMiss which is combined)
    let totalMeasured = measuredWidths.timestamp + measuredWidths.app + measuredWidths.events +
      measuredWidths.depot + measuredWidths.client + measuredWidths.speed +
      measuredWidths.cacheHit + measuredWidths.overall;

    if (isDatasourceShown) {
      totalMeasured += measuredWidths.datasource;
    }

    // If content fits, use measured widths; otherwise scale down proportionally
    let scaleFactor = 1;
    if (totalMeasured > availableWidth) {
      scaleFactor = availableWidth / totalMeasured;
    }

    // Define minimum widths for each column
    const minWidths = {
      timestamp: 80,
      app: 100,
      datasource: 50,
      events: 60,
      depot: 50,
      client: 70,
      speed: 60,
      cacheHit: 100,
      cacheMiss: 0,
      overall: 60
    };

    // Apply scaled widths, respecting minimums
    setColumnWidths({
      timestamp: Math.max(minWidths.timestamp, Math.floor(measuredWidths.timestamp * scaleFactor)),
      app: Math.max(minWidths.app, Math.floor(measuredWidths.app * scaleFactor)),
      datasource: Math.max(minWidths.datasource, Math.floor(measuredWidths.datasource * scaleFactor)),
      events: Math.max(minWidths.events, Math.floor(measuredWidths.events * scaleFactor)),
      depot: Math.max(minWidths.depot, Math.floor(measuredWidths.depot * scaleFactor)),
      client: Math.max(minWidths.client, Math.floor(measuredWidths.client * scaleFactor)),
      speed: Math.max(minWidths.speed, Math.floor(measuredWidths.speed * scaleFactor)),
      cacheHit: Math.max(minWidths.cacheHit, Math.floor(measuredWidths.cacheHit * scaleFactor)),
      cacheMiss: 0,
      overall: Math.max(minWidths.overall, Math.floor(measuredWidths.overall * scaleFactor))
    });
  }, [items, sortOrder, hasMultipleDatasources, showDatasourceLabels, smartDefaultWidths]);

  // Auto-fit a single column by measuring actual data content (not truncated DOM text)
  // Uses data from groupedItems to get full text values
  const handleAutoFitColumn = useCallback((column: keyof ColumnWidths) => {
    // Start with the smart default as baseline
    const defaultWidth = smartDefaultWidths[column];
    let maxWidth = defaultWidth;

    // Create a temporary span to measure text width
    const measureSpan = document.createElement('span');
    measureSpan.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;';
    document.body.appendChild(measureSpan);

    // Use the actual data to measure full text widths (not truncated DOM)
    const grouped = groupByDepot(items, sortOrder);

    // Set font based on column type
    switch (column) {
      case 'timestamp':
        measureSpan.style.font = '400 12px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          const startTime = formatDateTime(data.startTimeUtc);
          const endTime = formatDateTime(data.endTimeUtc);
          const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;
          measureSpan.textContent = timeRange;
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32); // padding
        });
        break;

      case 'app':
        measureSpan.style.font = '500 14px system-ui, -apple-system, sans-serif';
        grouped.forEach((data) => {
          measureSpan.textContent = data.gameName || data.service;
          // Add image width (120px) + gap (8px) + padding (32px)
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 120 + 8 + 32);
        });
        break;

      case 'client':
        measureSpan.style.font = '400 14px ui-monospace, monospace';
        grouped.forEach((data) => {
          if (data.clientsSet.size > 1) {
            measureSpan.textContent = `${data.clientsSet.size} clients`;
          } else {
            measureSpan.textContent = data.clientIp;
          }
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
        });
        break;

      case 'depot':
        measureSpan.style.font = '400 14px ui-monospace, monospace';
        grouped.forEach((data) => {
          measureSpan.textContent = data.depotId ? String(data.depotId) : 'N/A';
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
          measureSpan.textContent = data.datasource || 'N/A';
          maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
        });
        break;

      default:
        // For other columns, use smart defaults
        break;
    }

    // Also measure header
    measureSpan.style.font = '600 11px system-ui, -apple-system, sans-serif';
    const headerLabels: Record<string, string> = {
      timestamp: 'TIMESTAMP',
      app: 'APP',
      datasource: 'SOURCE',
      events: 'EVENTS',
      depot: 'DEPOT',
      client: 'CLIENT',
      speed: 'AVG SPEED',
      cacheHit: 'CACHE PERFORMANCE',
      cacheMiss: 'CACHE PERFORMANCE',
      overall: 'EFFICIENCY'
    };
    measureSpan.textContent = headerLabels[column] || '';
    maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);

    document.body.removeChild(measureSpan);

    // Update the column width - NO constraint, let it expand fully
    setColumnWidths(prev => ({
      ...prev,
      [column]: Math.max(60, Math.ceil(maxWidth)) // Ensure minimum of 60px
    }));
  }, [smartDefaultWidths, items, sortOrder]);

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
  useEffect(() => {
    const allDownloadIds = groupedItems.flatMap(group => group.downloadIds);
    if (allDownloadIds.length > 0) {
      fetchAssociations(allDownloadIds);
    }
  }, [groupedItems, fetchAssociations]);

  // Helper to get aggregated events for a depot group
  const getGroupEvents = useCallback((downloadIds: number[]): EventSummary[] => {
    const eventsMap = new Map<number, EventSummary>();
    downloadIds.forEach(id => {
      const associations = getAssociations(id);
      associations.events.forEach(event => {
        // Dedupe events by ID
        if (!eventsMap.has(event.id)) {
          eventsMap.set(event.id, event);
        }
      });
    });
    return Array.from(eventsMap.values());
  }, [getAssociations]);

  // Generate grid template from column widths
  // Use pixel values for precise control during resize, with 1fr on the last column to fill remaining space
  // Only show datasource column when there are multiple datasources
  const showDatasourceColumn = hasMultipleDatasources && showDatasourceLabels;
  const gridTemplate = showDatasourceColumn
    ? `${columnWidths.timestamp}px ${columnWidths.app + 40}px ${columnWidths.datasource}px ${columnWidths.events}px ${columnWidths.depot}px ${columnWidths.client}px ${columnWidths.speed}px ${columnWidths.cacheHit + columnWidths.cacheMiss}px minmax(${columnWidths.overall + 20}px, 1fr)`
    : `${columnWidths.timestamp}px ${columnWidths.app + 40}px ${columnWidths.events}px ${columnWidths.depot}px ${columnWidths.client}px ${columnWidths.speed}px ${columnWidths.cacheHit + columnWidths.cacheMiss}px minmax(${columnWidths.overall + 20}px, 1fr)`;

  // Get efficiency-based accent color
  const getAccentColor = (hitPercent: number) => {
    if (hitPercent >= 90) return 'var(--theme-success)';
    if (hitPercent >= 50) return 'var(--theme-warning)';
    return 'var(--theme-error)';
  };

  return (
      <div ref={containerRef} className="rounded-lg border overflow-hidden retro-table-container" style={{ borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-card-bg)' }}>
      {/* Keyframe styles for animations */}
      <style>{`
        @keyframes float {
          0%, 100% { transform: translateY(0); }
          50% { transform: translateY(-8px); }
        }
        @keyframes rowEntrance {
          from {
            opacity: 0;
            transform: translateX(-8px);
          }
          to {
            opacity: 1;
            transform: translateX(0);
          }
        }
        .row-animate {
          animation: rowEntrance 0.3s ease-out forwards;
          opacity: 0;
        }
      `}</style>

      {/* Desktop Table Header - only rendered on desktop via JS conditional */}
      {isDesktop && (
        <div
          className="grid gap-2 pl-3 pr-4 py-3 text-xs font-semibold uppercase tracking-wide border-b select-none sticky top-0 z-20"
          style={{
            gridTemplateColumns: gridTemplate,
            backgroundColor: 'var(--theme-bg-tertiary)',
            borderColor: 'var(--theme-border-secondary)',
            color: 'var(--theme-text-secondary)',
            minWidth: 'fit-content',
          }}
        >
          <div className="relative pr-2" data-header>
            Timestamp
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('timestamp', e)}
              onDoubleClick={() => handleAutoFitColumn('timestamp')}
            />
          </div>
          <div className="relative pr-2" data-header>
            App
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('app', e)}
              onDoubleClick={() => handleAutoFitColumn('app')}
            />
          </div>
          {showDatasourceColumn && (
            <div className="relative pr-2" data-header>
              Source
              <ResizeHandle
                onMouseDown={(e) => handleMouseDown('datasource', e)}
                onDoubleClick={() => handleAutoFitColumn('datasource')}
              />
            </div>
          )}
          <div className="relative pr-2" data-header>
            Events
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('events', e)}
              onDoubleClick={() => handleAutoFitColumn('events')}
            />
          </div>
          <div className="relative pr-2" data-header>
            Depot
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('depot', e)}
              onDoubleClick={() => handleAutoFitColumn('depot')}
            />
          </div>
          <div className="relative pr-2" data-header>
            Client
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('client', e)}
              onDoubleClick={() => handleAutoFitColumn('client')}
            />
          </div>
          <div className="relative pr-2" data-header>
            Avg Speed
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('speed', e)}
              onDoubleClick={() => handleAutoFitColumn('speed')}
            />
          </div>
          <div className="relative pr-2" data-header>
            Cache Performance
            <ResizeHandle
              onMouseDown={(e) => handleMouseDown('cacheHit', e)}
              onDoubleClick={() => handleAutoFitColumn('cacheHit')}
            />
          </div>
          <div className="flex items-center justify-center pr-2" data-header>
            <span>Efficiency</span>
          </div>
        </div>
      )}

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

          const startTime = formatDateTime(data.startTimeUtc);
          const endTime = formatDateTime(data.endTimeUtc);
          const timeRange = startTime === endTime ? startTime : `${startTime} - ${endTime}`;

          const accentColor = getAccentColor(hitPercent);

          return (
            <div
              key={data.id}
              className="row-animate transition-all duration-200 hover:bg-[var(--theme-bg-tertiary)]/50 group relative"
              style={{
                borderBottom: '1px solid var(--theme-border-secondary)',
                animationDelay: `${index * 30}ms`,
              }}
            >
              {/* Left accent border based on efficiency */}
              <div
                className="absolute left-0 top-0 bottom-0 w-1 transition-all duration-200 group-hover:w-1.5"
                style={{
                  backgroundColor: accentColor,
                  opacity: 0.7,
                }}
              />

              {/* Conditional Layout - Mobile or Desktop based on JS breakpoint detection */}
              {isDesktop ? (
                /* Desktop Layout */
                <div
                  className="grid gap-2 pl-4 pr-4 py-3 items-center"
                  style={{ gridTemplateColumns: gridTemplate }}
                  data-row
                >
                  {/* Timestamp */}
                  <div className="text-xs text-[var(--theme-text-secondary)] overflow-hidden whitespace-nowrap" data-cell>
                    <span className="block truncate" title={timeRange}>{timeRange}</span>
                  </div>

                  {/* App - with game image (responsive to column width) */}
                  <div className="flex items-center gap-2 overflow-hidden" data-cell>
                    {hasGameImage && data.gameAppId ? (
                      <img
                        src={`${API_BASE}/game-images/${data.gameAppId}/header/`}
                        alt={data.gameName || 'Game'}
                        className="min-w-[60px] max-w-[120px] w-2/5 h-auto aspect-[120/45] rounded object-cover flex-shrink transition-transform group-hover:scale-[1.02]"
                        loading="lazy"
                        onError={() => handleImageError(String(data.gameAppId))}
                      />
                    ) : (
                      /* Service icon placeholder - fixed size, no background box for cleaner shrinking */
                      <div className="w-10 h-10 flex items-center justify-center flex-shrink-0">
                        {getServiceIcon(data.service, 32)}
                      </div>
                    )}
                    <div className="flex flex-col min-w-0 overflow-hidden">
                      <span className="text-sm font-medium text-[var(--theme-text-primary)] truncate" title={data.gameName || data.service}>
                        {data.gameName || data.service}
                      </span>
                      {data.requestCount > 1 && (
                        <span className="text-xs text-[var(--theme-text-muted)]">
                          {data.clientsSet.size} client{data.clientsSet.size !== 1 ? 's' : ''} · {data.requestCount} request{data.requestCount !== 1 ? 's' : ''}
                        </span>
                      )}
                    </div>
                  </div>

                  {/* Datasource - only shown when multiple datasources exist */}
                  {showDatasourceColumn && (
                    <div className="overflow-hidden" data-cell>
                      <span
                        className="px-1.5 py-0.5 text-xs font-medium rounded inline-block truncate max-w-full"
                        style={{
                          backgroundColor: 'var(--theme-bg-tertiary)',
                          color: 'var(--theme-text-secondary)',
                          border: '1px solid var(--theme-border-secondary)'
                        }}
                        title={data.datasource}
                      >
                        {data.datasource || 'N/A'}
                      </span>
                    </div>
                  )}

                  {/* Events - shows event badges for associated downloads */}
                  <div className="overflow-hidden" data-cell>
                    {(() => {
                      const events = getGroupEvents(data.downloadIds);
                      return events.length > 0 ? (
                        <DownloadBadges events={events} maxVisible={2} size="sm" />
                      ) : (
                        <span className="text-xs text-[var(--theme-text-muted)]">—</span>
                      );
                    })()}
                  </div>

                  {/* Depot */}
                  <div className="overflow-hidden" data-cell>
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
                  <div className="text-sm font-mono text-[var(--theme-text-primary)] overflow-hidden" data-cell>
                    {data.clientsSet.size > 1 ? (
                      <span className="truncate block" title={`${data.clientsSet.size} clients`}>
                        {data.clientsSet.size} clients
                      </span>
                    ) : (
                      <ClientIpDisplay clientIp={data.clientIp} />
                    )}
                  </div>

                  {/* Avg Speed */}
                  <div className="text-sm text-[var(--theme-text-primary)] overflow-hidden flex items-center gap-1" data-cell>
                    <Zap size={12} style={{ color: 'var(--theme-warning)', opacity: 0.7 }} />
                    <span className="truncate">{formatSpeed(data.averageBytesPerSecond)}</span>
                  </div>

                  {/* Combined Cache Performance Bar */}
                  <div className="overflow-hidden pr-2" data-cell>
                    <CombinedProgressBar
                      hitBytes={cacheHitBytes}
                      missBytes={cacheMissBytes}
                      totalBytes={totalBytes}
                    />
                  </div>

                  {/* Circular Efficiency Gauge */}
                  <div className="flex justify-center" data-cell>
                    <EfficiencyGauge percent={hitPercent} />
                  </div>
                </div>
              ) : (
                /* Mobile Layout - with explicit width constraints */
                <div className="p-3 pl-4 space-y-2 sm:space-y-3 w-full max-w-full overflow-hidden">
                  {/* App image and name */}
                  <div className="flex items-center gap-2 sm:gap-3 w-full min-w-0">
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
                    <div className="flex-1 min-w-0 overflow-hidden">
                      <div className="text-sm font-medium text-[var(--theme-text-primary)] truncate">
                        {data.gameName || data.service}
                        {data.requestCount > 1 && (
                          <span className="ml-2 text-xs text-[var(--theme-text-muted)]">
                            ({data.clientsSet.size} client{data.clientsSet.size !== 1 ? 's' : ''} · {data.requestCount} request{data.requestCount !== 1 ? 's' : ''})
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
                  <div className="flex items-center justify-between text-xs text-[var(--theme-text-secondary)] min-w-0">
                    <span className="truncate mr-2">{timeRange}</span>
                    <span className="flex items-center gap-1 text-[var(--theme-text-primary)] flex-shrink-0">
                      <Zap size={12} style={{ color: 'var(--theme-warning)' }} />
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

      {groupedItems.length === 0 && <EmptyState />}
      </div>
  );
});

RetroView.displayName = 'RetroView';

export default RetroView;
