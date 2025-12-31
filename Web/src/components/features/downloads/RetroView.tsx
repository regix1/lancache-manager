import React, { useState, useRef, useCallback, useEffect, useMemo } from 'react';
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
import type { Download as DownloadType, DownloadGroup } from '../../../types';

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
    <div className="flex flex-col gap-1.5 min-w-0 w-full">
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
      {/* Labels */}
      {showLabels && (
        <div className="flex justify-between gap-2 text-[10px] min-w-0 w-full overflow-hidden">
          <span className="truncate flex-1 min-w-0" style={{ color: 'var(--theme-chart-cache-hit)' }}>
            {formatBytes(hitBytes)} ({formatPercent(hitPercent)})
          </span>
          <span className="truncate flex-1 min-w-0 text-right" style={{ color: 'var(--theme-error)' }}>
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
  const [imageErrors, setImageErrors] = useState<Set<string>>(new Set());

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
    setColumnWidths(smartDefaultWidths);
  }, [smartDefaultWidths]);

  // Auto-fit a single column by measuring actual DOM content
  // Falls back to smart defaults from textMeasurement.ts if DOM measurement fails
  // Constrains to available container width to prevent horizontal scroll
  const handleAutoFitColumn = useCallback((column: keyof ColumnWidths) => {
    // Start with the smart default as baseline
    const defaultWidth = smartDefaultWidths[column];
    let maxWidth = defaultWidth;

    if (!containerRef.current) {
      // Fall back to smart defaults if no container
      setColumnWidths(prev => ({
        ...prev,
        [column]: defaultWidth
      }));
      return;
    }

    // Calculate available width constraint
    const containerWidth = containerRef.current.clientWidth;
    const GRID_GAP = 8; // gap-2 = 0.5rem = 8px
    const PADDING = 32; // pl-4 + pr-4 = 32px
    const NUM_GAPS = 6; // 7 columns = 6 gaps

    // Calculate width used by other columns (excluding the one being auto-fit)
    // Grid template: timestamp, app+40, depot, client, speed, cacheHit+cacheMiss, overall+20
    let otherColumnsWidth = 0;

    // Add each column's contribution to the grid
    if (column !== 'timestamp') otherColumnsWidth += columnWidths.timestamp;
    if (column !== 'app') otherColumnsWidth += columnWidths.app + 40;
    if (column !== 'depot') otherColumnsWidth += columnWidths.depot;
    if (column !== 'client') otherColumnsWidth += columnWidths.client;
    if (column !== 'speed') otherColumnsWidth += columnWidths.speed;

    // Cache column is combined (cacheHit + cacheMiss)
    if (column !== 'cacheHit' && column !== 'cacheMiss') {
      otherColumnsWidth += columnWidths.cacheHit + columnWidths.cacheMiss;
    } else {
      // When auto-fitting cache, still need to account for the other half
      otherColumnsWidth += columnWidths.cacheMiss; // cacheMiss stays, only cacheHit changes
    }

    // Overall column (minimum width, can grow with 1fr but we use minimum for calculation)
    if (column !== 'overall') otherColumnsWidth += columnWidths.overall + 20;

    // Maximum available width for this column
    const maxAvailableWidth = containerWidth - otherColumnsWidth - PADDING - (NUM_GAPS * GRID_GAP);

    // Map column names to their grid index
    const columnIndexMap: Record<keyof ColumnWidths, number> = {
      timestamp: 0,
      app: 1,
      depot: 2,
      client: 3,
      speed: 4,
      cacheHit: 5,
      cacheMiss: 5, // Combined with cacheHit
      overall: 6
    };

    const colIndex = columnIndexMap[column];

    // Create a temporary span to measure text width
    const measureSpan = document.createElement('span');
    measureSpan.style.cssText = 'position:absolute;visibility:hidden;white-space:nowrap;font:inherit;';
    document.body.appendChild(measureSpan);

    // Find all rows and measure the content in the target column
    const rows = containerRef.current.querySelectorAll('[data-row]');
    rows.forEach((row) => {
      const cells = row.querySelectorAll('[data-cell]');
      const cell = cells[colIndex];
      if (cell) {
        // Get computed style to match font
        const computedStyle = window.getComputedStyle(cell);
        measureSpan.style.font = computedStyle.font;
        measureSpan.style.fontSize = computedStyle.fontSize;
        measureSpan.style.fontFamily = computedStyle.fontFamily;
        measureSpan.style.fontWeight = computedStyle.fontWeight;

        // Measure text content
        measureSpan.textContent = cell.textContent || '';
        const textWidth = measureSpan.offsetWidth;

        // For app column, add image width
        if (column === 'app') {
          const img = cell.querySelector('img');
          if (img) {
            maxWidth = Math.max(maxWidth, textWidth + 120 + 16 + 40); // image + gap + padding + tag space
          } else {
            maxWidth = Math.max(maxWidth, textWidth + 120 + 16 + 40); // icon placeholder + gap + padding
          }
        } else {
          maxWidth = Math.max(maxWidth, textWidth + 32); // Add padding
        }
      }
    });

    // Also measure header
    const headers = containerRef.current.querySelectorAll('[data-header]');
    const header = headers[colIndex];
    if (header) {
      const computedStyle = window.getComputedStyle(header);
      measureSpan.style.font = computedStyle.font;
      measureSpan.textContent = header.textContent || '';
      maxWidth = Math.max(maxWidth, measureSpan.offsetWidth + 32);
    }

    document.body.removeChild(measureSpan);

    // Constrain to available width (prevent horizontal scroll)
    const constrainedWidth = Math.min(Math.ceil(maxWidth), maxAvailableWidth);

    // Update the column width
    setColumnWidths(prev => ({
      ...prev,
      [column]: Math.max(60, constrainedWidth) // Ensure minimum of 60px
    }));
  }, [smartDefaultWidths, columnWidths]);

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

  // Generate grid template from column widths
  // Use pixel values for precise control during resize, with 1fr on the last column to fill remaining space
  const gridTemplate = `${columnWidths.timestamp}px ${columnWidths.app + 40}px ${columnWidths.depot}px ${columnWidths.client}px ${columnWidths.speed}px ${columnWidths.cacheHit + columnWidths.cacheMiss}px minmax(${columnWidths.overall + 20}px, 1fr)`;

  // Calculate minimum table width to ensure header background extends across full scroll area
  const GRID_GAP_PX = 8;
  const PADDING_PX = 32; // pl-3 + pr-4 ≈ 28px, round up
  const NUM_COLUMNS = 7;
  const minTableWidth = columnWidths.timestamp + (columnWidths.app + 40) + columnWidths.depot +
    columnWidths.client + columnWidths.speed + (columnWidths.cacheHit + columnWidths.cacheMiss) +
    (columnWidths.overall + 20) + ((NUM_COLUMNS - 1) * GRID_GAP_PX) + PADDING_PX;

  // Get efficiency-based accent color
  const getAccentColor = (hitPercent: number) => {
    if (hitPercent >= 90) return 'var(--theme-success)';
    if (hitPercent >= 50) return 'var(--theme-warning)';
    return 'var(--theme-error)';
  };

  return (
    <div ref={containerRef} className="rounded-lg border overflow-hidden retro-table-container" style={{ borderColor: 'var(--theme-border-primary)', backgroundColor: 'var(--theme-card-bg)' }}>
      {/* Keyframe styles for animations and mobile layout fixes */}
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

        /* Mobile layout constraints - force content to fit viewport */
        @media (max-width: 1023px) {
          .retro-table-container {
            max-width: 100%;
          }
          .retro-table-container .retro-mobile-row {
            max-width: 100%;
            box-sizing: border-box;
          }
          .retro-table-container .retro-mobile-content {
            max-width: 100%;
            box-sizing: border-box;
          }
          /* Ensure desktop layout doesn't affect layout */
          .retro-table-container .retro-desktop-layout {
            display: none !important;
          }
        }
      `}</style>

      {/* Desktop Table Header - hidden on mobile */}
      <div
        className="retro-desktop-layout hidden lg:grid gap-2 pl-3 pr-4 py-3 text-xs font-semibold uppercase tracking-wide border-b select-none sticky top-0 z-20"
        style={{
          gridTemplateColumns: gridTemplate,
          minWidth: minTableWidth,
          backgroundColor: 'var(--theme-bg-tertiary)',
          borderColor: 'var(--theme-border-secondary)',
          color: 'var(--theme-text-secondary)',
          backdropFilter: 'blur(8px)',
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
        <div className="flex items-center justify-center gap-1 pr-2" data-header>
          <span>Efficiency</span>
          <Tooltip content="Reset column widths to default">
            <button
              onClick={handleResetWidths}
              className="p-0.5 rounded text-themed-muted hover:text-themed-primary transition-colors flex-shrink-0"
              style={{ fontSize: '10px' }}
            >
              ↺
            </button>
          </Tooltip>
        </div>
      </div>

      {/* Table Body */}
      <div className="overflow-x-hidden">
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
              className="row-animate retro-mobile-row transition-all duration-200 hover:bg-[var(--theme-bg-tertiary)]/50 group relative w-full"
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

              {/* Mobile Layout */}
              <div className="retro-mobile-content lg:hidden p-3 pl-4 space-y-2 sm:space-y-3 overflow-hidden min-w-0 max-w-full">
                {/* App image and name */}
                <div className="flex items-center gap-2 sm:gap-3 min-w-0 w-full">
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
                    <div className="flex items-center gap-2 text-xs text-[var(--theme-text-muted)] min-w-0 overflow-hidden">
                      <span className="truncate min-w-0">
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
                <div className="flex items-center justify-between gap-2 text-xs text-[var(--theme-text-secondary)] min-w-0 w-full">
                  <span className="truncate min-w-0 flex-1">{timeRange}</span>
                  <span className="flex items-center gap-1 text-[var(--theme-text-primary)] flex-shrink-0">
                    <Zap size={12} style={{ color: 'var(--theme-warning)' }} />
                    {formatSpeed(data.averageBytesPerSecond)}
                  </span>
                </div>

                {/* Combined Progress Bar and Efficiency */}
                <div className="flex items-center gap-3 min-w-0 w-full">
                  <div className="flex-1 min-w-0">
                    <CombinedProgressBar
                      hitBytes={cacheHitBytes}
                      missBytes={cacheMissBytes}
                      totalBytes={totalBytes}
                      showLabels={false}
                    />
                  </div>
                  <div className="flex-shrink-0">
                    <EfficiencyGauge percent={hitPercent} size={44} />
                  </div>
                </div>
              </div>

              {/* Desktop Layout */}
              <div
                className="retro-desktop-layout hidden lg:grid gap-2 pl-4 pr-4 py-3 items-center"
                style={{ gridTemplateColumns: gridTemplate, minWidth: minTableWidth }}
                data-row
              >
                {/* Timestamp */}
                <div className="text-xs text-[var(--theme-text-secondary)] overflow-hidden whitespace-nowrap" data-cell>
                  <span className="block truncate" title={timeRange}>{timeRange}</span>
                </div>

                {/* App - with game image */}
                <div className="flex items-center gap-2 overflow-hidden" data-cell>
                  {hasGameImage && data.gameAppId ? (
                    <img
                      src={`${API_BASE}/game-images/${data.gameAppId}/header/`}
                      alt={data.gameName || 'Game'}
                      className="w-[120px] h-[45px] rounded object-cover flex-shrink-0 transition-transform group-hover:scale-[1.02]"
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
            </div>
          );
        })}
      </div>

      {groupedItems.length === 0 && <EmptyState />}
    </div>
  );
};

export default RetroView;
